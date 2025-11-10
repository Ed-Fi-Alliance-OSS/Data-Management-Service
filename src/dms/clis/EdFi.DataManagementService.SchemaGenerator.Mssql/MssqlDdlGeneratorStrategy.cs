// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using System.Text;
using EdFi.DataManagementService.SchemaGenerator.Abstractions;
using HandlebarsDotNet;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.SchemaGenerator.Mssql
{
    /// <summary>
    /// SQL Server DDL generation strategy implementation.
    /// </summary>
    public class MssqlDdlGeneratorStrategy(ILogger<MssqlDdlGeneratorStrategy> _logger) : IDdlGeneratorStrategy
    {
        /// <summary>
        /// Generates SQL Server DDL scripts for the given ApiSchema metadata.
        /// </summary>
        /// <param name="apiSchema">The deserialized ApiSchema metadata object.</param>
        /// <param name="outputDirectory">The directory to write output scripts to.</param>
        /// <param name="includeExtensions">Whether to include extensions in the DDL.</param>
        public void GenerateDdl(
            ApiSchema apiSchema,
            string outputDirectory,
            bool includeExtensions,
            bool skipUnionViews = false
        )
        {
            Directory.CreateDirectory(outputDirectory);

            var ddl = GenerateDdlString(apiSchema, includeExtensions, skipUnionViews);

            File.WriteAllText(Path.Combine(outputDirectory, "EdFi-DMS-Database-Schema-SQLServer.sql"), ddl);
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

            File.WriteAllText(Path.Combine(outputDirectory, "EdFi-DMS-Database-Schema-SQLServer.sql"), ddl);
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
        public string GenerateDdlString(
            ApiSchema apiSchema,
            bool includeExtensions,
            bool skipUnionViews = false
        )
        {
            if (apiSchema.ProjectSchema == null || apiSchema.ProjectSchema.ResourceSchemas == null)
            {
                throw new InvalidDataException("ApiSchema does not contain valid projectSchema.");
            }

            var options = new DdlGenerationOptions
            {
                IncludeExtensions = includeExtensions,
                SkipUnionViews = skipUnionViews,
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
            var templatePath = Path.Combine(
                AppContext.BaseDirectory,
                "Templates",
                "mssql-table-idempotent.hbs"
            );
            if (!File.Exists(templatePath))
            {
                templatePath = Path.Combine(
                    Directory.GetCurrentDirectory(),
                    "Templates",
                    "mssql-table-idempotent.hbs"
                );
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
                    if (
                        !options.IncludeExtensions && resourceSchema.FlatteningMetadata.Table.IsExtensionTable
                    )
                    {
                        continue;
                    }

                    var schemaName = DetermineSchemaName(apiSchema.ProjectSchema, resourceSchema, options);

                    // Only add schema to usedSchemas if we're going to use separate schemas
                    if (ShouldUseSeparateSchema(schemaName, options, resourceSchema))
                    {
                        usedSchemas.Add(schemaName);
                    }
                }
            }

            // Generate CREATE SCHEMA statements
            foreach (var schema in usedSchemas.OrderBy(s => s))
            {
                sb.AppendLine($"IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = '{schema}')");
                sb.AppendLine("BEGIN");
                sb.AppendLine($"    EXEC('CREATE SCHEMA [{schema}]');");
                sb.AppendLine($"    PRINT 'Schema {schema} created.';");
                sb.AppendLine("END");
                sb.AppendLine("ELSE");
                sb.AppendLine($"    PRINT 'Schema {schema} already exists, skipped.';");
                sb.AppendLine("GO");
                sb.AppendLine();
            }

            // PASS 1: Generate tables WITHOUT foreign key constraints
            var fkConstraintsToAdd = new List<(string tableName, string schemaName, object fkConstraint)>();

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

                var originalSchemaName = GetOriginalSchemaName(
                    apiSchema.ProjectSchema,
                    resourceSchema,
                    options
                );

                // Generate DDL for tables without FK constraints
                GenerateTableDdlWithoutForeignKeys(
                    resourceSchema.FlatteningMetadata.Table,
                    template,
                    sb,
                    null,
                    null,
                    originalSchemaName,
                    options,
                    resourceSchema,
                    fkConstraintsToAdd,
                    apiSchema.ProjectSchema
                );
            }

            // PASS 2: Add foreign key constraints via ALTER TABLE statements (idempotent)
            if (options.GenerateForeignKeyConstraints && fkConstraintsToAdd.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("-- Foreign Key Constraints");
                sb.AppendLine();

                foreach (var (tableName, schemaName, fkConstraint) in fkConstraintsToAdd)
                {
                    var constraint = (dynamic)fkConstraint;
                    var constraintName = MssqlNamingHelper.MakeMssqlIdentifier(constraint.constraintName);
                    sb.AppendLine(
                        $"IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE name = '{constraintName}')"
                    );
                    sb.AppendLine("BEGIN");
                    // Fix: Properly format multi-column FKs for MSSQL ([col1], [col2], ...)
                    string FormatColumns(object col)
                    {
                        if (col == null)
                        {
                            return string.Empty;
                        }
                        var str = col.ToString();
                        if (string.IsNullOrWhiteSpace(str))
                        {
                            return string.Empty;
                        }
                        var cols = str.Split(',')
                            .Select(s => s.Trim())
                            .Where(s => !string.IsNullOrEmpty(s))
                            .ToArray();
                        return string.Join(
                            ", ",
                            cols.Select(c => c.StartsWith("[") ? c : $"[{c}]").ToArray()
                        );
                    }
                    string fkCols = FormatColumns(constraint.column);
                    string pkCols = FormatColumns(constraint.parentColumn);
                    sb.AppendLine(
                        $"    ALTER TABLE [{schemaName}].[{tableName}] ADD CONSTRAINT [{constraintName}] FOREIGN KEY ({fkCols}) REFERENCES {constraint.parentTable}({pkCols}){(constraint.cascade ? " ON DELETE CASCADE" : "")};"
                    );
                    sb.AppendLine($"    PRINT 'Foreign key constraint {constraintName} created.';");
                    sb.AppendLine("END");
                    sb.AppendLine("ELSE");
                    sb.AppendLine(
                        $"    PRINT 'Foreign key constraint {constraintName} already exists, skipped.';"
                    );
                    sb.AppendLine("GO");
                    sb.AppendLine();
                }
            }

            // PASS 3: Generate natural key resolution views
            if (!options.SkipNaturalKeyViews)
            {
                var naturalKeyViewTemplatePath = Path.Combine(
                    AppContext.BaseDirectory,
                    "Templates",
                    "mssql-natural-key-view.hbs"
                );
                if (!File.Exists(naturalKeyViewTemplatePath))
                {
                    naturalKeyViewTemplatePath = Path.Combine(
                        Directory.GetCurrentDirectory(),
                        "Templates",
                        "mssql-natural-key-view.hbs"
                    );
                }
                var naturalKeyViewTemplateContent = File.ReadAllText(naturalKeyViewTemplatePath);
                var naturalKeyViewTemplate = Handlebars.Compile(naturalKeyViewTemplateContent);

                bool headerAdded = false;

                foreach (var kvp in apiSchema.ProjectSchema.ResourceSchemas ?? [])
                {
                    var resourceName = kvp.Key;
                    var resourceSchema = kvp.Value;

                    if (resourceSchema.FlatteningMetadata?.Table == null)
                    {
                        continue;
                    }

                    // Skip extensions if not requested
                    if (
                        !options.IncludeExtensions && resourceSchema.FlatteningMetadata.Table.IsExtensionTable
                    )
                    {
                        continue;
                    }

                    // Add header only when we have views to generate
                    if (!headerAdded)
                    {
                        sb.AppendLine();
                        sb.AppendLine("-- Natural Key Resolution Views");
                        sb.AppendLine();
                        headerAdded = true;
                    }

                    var originalSchemaName = GetOriginalSchemaName(
                        apiSchema.ProjectSchema,
                        resourceSchema,
                        options
                    );

                    // Generate views for the root table and all child tables
                    GenerateNaturalKeyViews(
                        resourceSchema.FlatteningMetadata.Table,
                        naturalKeyViewTemplate,
                        sb,
                        null,
                        null, // No parent for root tables
                        originalSchemaName,
                        options,
                        resourceSchema,
                        apiSchema.ProjectSchema
                    );
                }
            }

            // Generate union views for abstract resources unless skipped
            if (!options.SkipUnionViews && apiSchema.ProjectSchema != null)
            {
                var unionViewTemplatePath = Path.Combine(
                    AppContext.BaseDirectory,
                    "Templates",
                    "mssql-union-view.hbs"
                );
                if (!File.Exists(unionViewTemplatePath))
                {
                    unionViewTemplatePath = Path.Combine(
                        Directory.GetCurrentDirectory(),
                        "Templates",
                        "mssql-union-view.hbs"
                    );
                }
                var unionViewTemplateContent = File.ReadAllText(unionViewTemplatePath);
                var unionViewTemplate = Handlebars.Compile(unionViewTemplateContent);

                // Approach 1: Look for abstractResources in the project schema
                var projectSchemaNode = System
                    .Text.Json.JsonDocument.Parse(
                        System.Text.Json.JsonSerializer.Serialize(apiSchema.ProjectSchema)
                    )
                    .RootElement;
                if (projectSchemaNode.TryGetProperty("abstractResources", out var abstractResourcesNode))
                {
                    foreach (var abstractResourceProp in abstractResourcesNode.EnumerateObject())
                    {
                        var abstractResource = abstractResourceProp.Value;
                        if (
                            abstractResource.TryGetProperty("flatteningMetadata", out var flatteningMetadata)
                            && flatteningMetadata.TryGetProperty("subclassTypes", out var subclassTypesNode)
                            && flatteningMetadata.TryGetProperty("unionViewName", out var unionViewNameNode)
                        )
                        {
                            var subclassTypes = new List<string>();
                            foreach (var subclass in subclassTypesNode.EnumerateArray())
                            {
                                subclassTypes.Add(subclass.GetString() ?? "");
                            }

                            var unionViewName = unionViewNameNode.GetString() ?? "";

                            // Build select statements for each subclass - compute intersection of common columns
                            var selectStatements = new List<string>();
                            string? viewSchemaName = null; // Track schema name for view prefixing

                            // List of per-subclass column name sets
                            var subclassColumnSets = new List<HashSet<string>>();
                            var subclassInfos = new List<(string tableRef, string discriminator)>();

                            foreach (var subclassType in subclassTypes)
                            {
                                if (
                                    apiSchema.ProjectSchema.ResourceSchemas?.TryGetValue(
                                        subclassType,
                                        out var subclassSchema
                                    ) == true
                                    && subclassSchema.FlatteningMetadata?.Table != null
                                )
                                {
                                    var table = subclassSchema.FlatteningMetadata.Table;
                                    var originalSchemaName = GetOriginalSchemaName(
                                        apiSchema.ProjectSchema,
                                        subclassSchema,
                                        options
                                    );
                                    viewSchemaName ??= originalSchemaName;
                                    var tableName = DetermineTableName(
                                        table.BaseName,
                                        originalSchemaName,
                                        subclassSchema,
                                        options
                                    );
                                    var finalSchemaName = options.ResolveSchemaName(null);
                                    var tableRef = $"{finalSchemaName}.[{tableName}]";
                                    var discriminator = table.DiscriminatorValue ?? subclassType;

                                    // Collect column names for this subclass (exclude system columns)
                                    var cols = (
                                        table.Columns ?? System.Linq.Enumerable.Empty<ColumnMetadata>()
                                    )
                                        .Where(c => !c.IsParentReference)
                                        .Select(c => c.ColumnName)
                                        .Where(n => !string.IsNullOrEmpty(n))
                                        .Select(n => n.Trim())
                                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                                    subclassColumnSets.Add(cols);
                                    subclassInfos.Add((tableRef, discriminator));
                                }
                            }

                            // Compute intersection of subclass columns (if any subclasses found)
                            HashSet<string> commonColumns = new(StringComparer.OrdinalIgnoreCase);
                            if (subclassColumnSets.Count > 0)
                            {
                                commonColumns = new HashSet<string>(
                                    subclassColumnSets[0],
                                    StringComparer.OrdinalIgnoreCase
                                );
                                foreach (var s in subclassColumnSets.Skip(1))
                                {
                                    commonColumns.IntersectWith(s);
                                }
                            }

                            // Ensure we always include Document and audit columns via SELECT literal; commonColumns only holds extra common user columns
                            foreach (var (tableRef, discriminator) in subclassInfos)
                            {
                                // Emit unquoted identifiers for union view selects and alias any IsSuperclassIdentity column
                                var colsList = new List<string> { "Id" };

                                // Attempt to find matching subclass schema by comparing unquoted table names
                                string unquotedTableName = tableRef.Split('.').Last().Trim('[', ']');
                                ResourceSchema? matchingSchema = null;
                                foreach (var st in subclassTypes)
                                {
                                    if (
                                        apiSchema.ProjectSchema.ResourceSchemas?.TryGetValue(st, out var ss)
                                            == true
                                        && ss.FlatteningMetadata?.Table != null
                                    )
                                    {
                                        var candidateName = DetermineTableName(
                                            ss.FlatteningMetadata.Table.BaseName,
                                            GetOriginalSchemaName(apiSchema.ProjectSchema, ss, options),
                                            ss,
                                            options
                                        );
                                        candidateName = candidateName.Trim('[', ']');
                                        if (
                                            string.Equals(
                                                candidateName,
                                                unquotedTableName,
                                                StringComparison.Ordinal
                                            )
                                        )
                                        {
                                            matchingSchema = ss;
                                            break;
                                        }
                                    }
                                }

                                string viewNaturalKeyName = unionViewName + "Id";
                                if (matchingSchema != null)
                                {
                                    var supCol = (
                                        matchingSchema.FlatteningMetadata?.Table?.Columns
                                        ?? System.Linq.Enumerable.Empty<ColumnMetadata>()
                                    ).FirstOrDefault(c => c.IsSuperclassIdentity);
                                    if (supCol != null && !string.IsNullOrEmpty(supCol.ColumnName))
                                    {
                                        colsList.Add($"{supCol.ColumnName} AS {viewNaturalKeyName}");
                                    }
                                }

                                // Add remaining common columns (avoid adding the superclass identity twice)
                                var matchingCols =
                                    matchingSchema?.FlatteningMetadata?.Table?.Columns
                                    ?? System.Linq.Enumerable.Empty<ColumnMetadata>();
                                colsList.AddRange(
                                    commonColumns.Where(c =>
                                        matchingSchema == null
                                        || !matchingCols.Any(col =>
                                            col.IsSuperclassIdentity
                                            && string.Equals(
                                                col.ColumnName,
                                                c,
                                                StringComparison.OrdinalIgnoreCase
                                            )
                                        )
                                    )
                                );

                                var documentColumns = "Document_Id, Document_PartitionKey";
                                if (options.IncludeAuditColumns)
                                {
                                    documentColumns += ", CreateDate, LastModifiedDate, ChangeVersion";
                                }

                                var selectCols = string.Join(", ", colsList);
                                // Escape single quotes for EXEC('...') context
                                selectStatements.Add(
                                    $"SELECT {selectCols}, ''{discriminator}'' AS Discriminator, {documentColumns} FROM {tableRef}"
                                );
                            }

                            // Only generate view if we have select statements
                            if (selectStatements.Any())
                            {
                                // Apply schema prefix to view name if using prefixed table names
                                var finalViewName = DetermineTableName(
                                    unionViewName,
                                    viewSchemaName ?? "edfi",
                                    null,
                                    options
                                );
                                var viewData = new { viewName = finalViewName, selectStatements };
                                sb.AppendLine(unionViewTemplate(viewData));
                            }
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
                        bool hasPolymorphicRef = (
                            table.Columns ?? System.Linq.Enumerable.Empty<ColumnMetadata>()
                        ).Any(c => c.IsPolymorphicReference);
                        bool hasDiscriminator = (
                            table.Columns ?? System.Linq.Enumerable.Empty<ColumnMetadata>()
                        ).Any(c => c.IsDiscriminator);
                        bool hasChildTablesWithDiscriminatorValues = (
                            table.ChildTables ?? System.Linq.Enumerable.Empty<TableMetadata>()
                        ).Any(ct => !string.IsNullOrEmpty(ct.DiscriminatorValue));

                        if (hasPolymorphicRef && hasDiscriminator && hasChildTablesWithDiscriminatorValues)
                        {
                            // Generate union view for this polymorphic reference
                            var viewName = table.BaseName;
                            var selectStatements = new List<string>();

                            // Get the natural key columns from the parent table (these should be common across all child tables)
                            var naturalKeyColumns = (
                                table.Columns ?? System.Linq.Enumerable.Empty<ColumnMetadata>()
                            )
                                .Where(c => c.IsNaturalKey)
                                .Select(c => c.ColumnName)
                                .ToList();

                            foreach (
                                var childTable in (
                                    table.ChildTables ?? System.Linq.Enumerable.Empty<TableMetadata>()
                                ).Where(ct => !string.IsNullOrEmpty(ct.DiscriminatorValue))
                            )
                            {
                                // Emit unquoted identifiers and include IsSuperclassIdentity alias where applicable
                                var columns = new List<string> { "Id" };
                                columns.AddRange(
                                    (
                                        childTable.Columns ?? System.Linq.Enumerable.Empty<ColumnMetadata>()
                                    ).Select(c => c.ColumnName)
                                );

                                var selectCols = string.Join(", ", columns);
                                var discriminatorValue = childTable.DiscriminatorValue;
                                // Build unquoted table ref: use final schema (resolved) and unquoted table name
                                var originalSchemaName = GetOriginalSchemaName(
                                    apiSchema.ProjectSchema,
                                    resourceSchema,
                                    options
                                );
                                var tableName = DetermineTableName(
                                    childTable.BaseName,
                                    originalSchemaName,
                                    resourceSchema,
                                    options
                                );
                                var finalSchemaName = options.ResolveSchemaName(null);
                                var tableRef = $"{finalSchemaName}.{tableName}";

                                // Build SELECT statement with audit columns if enabled
                                var documentColumns = "Document_Id, Document_PartitionKey";
                                if (options.IncludeAuditColumns)
                                {
                                    documentColumns += ", CreateDate, LastModifiedDate, ChangeVersion";
                                }
                                // Escape single quotes for EXEC('...') context
                                selectStatements.Add(
                                    $"SELECT {selectCols}, ''{discriminatorValue}'' AS Discriminator, {documentColumns} FROM {tableRef}"
                                );
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

        private void GenerateTableDdlWithoutForeignKeys(
            TableMetadata table,
            HandlebarsTemplate<object, object> template,
            StringBuilder sb,
            string? parentTableName,
            List<string>? parentPkColumns,
            string originalSchemaName,
            DdlGenerationOptions options,
            ResourceSchema? resourceSchema,
            List<(string tableName, string schemaName, object fkConstraint)> fkConstraintsToAdd,
            ProjectSchema? projectSchema = null
        )
        {
            var tableName = MssqlNamingHelper.MakeMssqlIdentifier(
                DetermineTableName(table.BaseName, originalSchemaName, resourceSchema, options)
            );

            // Ensure collections are non-null to avoid CS8602 when metadata is incomplete
            table.Columns = table.Columns ?? new List<ColumnMetadata>();
            table.ChildTables = table.ChildTables ?? new List<TableMetadata>();

            // For extension resources and descriptor resources using separate schemas, use the original schema as final schema
            var finalSchemaName = ShouldUseSeparateSchema(originalSchemaName, options, resourceSchema)
                ? originalSchemaName
                : options.ResolveSchemaName(null);
            finalSchemaName = MssqlNamingHelper.MakeMssqlIdentifier(finalSchemaName);

            var isRootTable = parentTableName == null;

            // Track cross-resource references for FK generation
            var crossResourceReferences = new List<(string columnName, string referencedResource)>();

            // Generate data columns (excluding parent references and system columns)
            var columns = table
                .Columns.Where(c => !c.IsParentReference) // Parent FKs handled separately
                .Select(c =>
                {
                    // Track cross-resource references for later FK generation
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
                        name = MssqlNamingHelper.MakeMssqlIdentifier(c.ColumnName),
                        type = MapColumnType(c),
                        isRequired = c.IsRequired,
                    };
                })
                .ToList();

            // Natural key columns for unique constraint
            var naturalKeyColumns = table
                .Columns.Where(c => c.IsNaturalKey && !c.IsParentReference)
                .Select(c => MssqlNamingHelper.MakeMssqlIdentifier(c.ColumnName))
                .ToList();

            // Add parent FK column for child tables
            if (parentTableName != null)
            {
                var parentFkColumn = MssqlNamingHelper.MakeMssqlIdentifier($"{parentTableName}_Id");
                columns.Insert(
                    0,
                    new
                    {
                        name = parentFkColumn,
                        type = "BIGINT",
                        isRequired = true,
                    }
                );

                // Store FK constraint for later generation
                fkConstraintsToAdd.Add(
                    (
                        tableName,
                        finalSchemaName,
                        new
                        {
                            constraintName = MssqlNamingHelper.MakeMssqlIdentifier(
                                $"FK_{table.BaseName}_{parentTableName}"
                            ),
                            column = parentFkColumn,
                            parentTable = $"[{finalSchemaName}].[{MssqlNamingHelper.MakeMssqlIdentifier(DetermineTableName(parentTableName, originalSchemaName, resourceSchema, options))}]",
                            parentColumn = "Id",
                            cascade = true,
                        }
                    )
                );
            }

            // Add FK constraints for descriptor columns to dms.Descriptor(Id)
            foreach (
                var descriptorCol in table.Columns.Where(c =>
                    string.Equals(c.ColumnType, "descriptor", StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                // If table name starts with edfi_ (case-insensitive), use edfi_Descriptor, else Descriptor
                var descriptorTable = tableName.StartsWith("edfi_", StringComparison.OrdinalIgnoreCase)
                    ? $"[{finalSchemaName}].[{MssqlNamingHelper.MakeMssqlIdentifier("edfi_Descriptor")}]"
                    : $"[{finalSchemaName}].[{MssqlNamingHelper.MakeMssqlIdentifier("Descriptor")}]";

                fkConstraintsToAdd.Add(
                    (
                        tableName,
                        finalSchemaName,
                        new
                        {
                            constraintName = MssqlNamingHelper.MakeMssqlIdentifier(
                                $"FK_{table.BaseName}_{descriptorCol.ColumnName}_Descriptor"
                            ),
                            column = MssqlNamingHelper.MakeMssqlIdentifier(descriptorCol.ColumnName),
                            parentTable = descriptorTable,
                            parentColumn = "Id",
                            cascade = false,
                        }
                    )
                );
            }

            // Add FK constraints for columns that follow the pattern <TableName>_Id
            // These reference other tables (similar to view joins)
            if (projectSchema != null)
            {
                var naturalKeyReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (
                    var column in table.Columns.Where(c =>
                        !c.IsParentReference
                        && c.ColumnName.EndsWith("_Id", StringComparison.OrdinalIgnoreCase)
                        && string.IsNullOrEmpty(c.FromReferencePath)
                    )
                )
                {
                    // Extract potential table name from column name (e.g., School_Id -> School)
                    // For prefixed columns like Participating_EducationOrganization_Id or BalanceSheet_BalanceSheetDimension_Id,
                    // extract the rightmost segment (the actual table name)
                    var columnNameWithoutId = column.ColumnName.Substring(0, column.ColumnName.Length - 3);

                    // Find the rightmost segment after the last underscore (this is the potential table name)
                    var lastUnderscoreIndex = columnNameWithoutId.LastIndexOf('_');
                    var potentialTableName =
                        lastUnderscoreIndex >= 0
                            ? columnNameWithoutId.Substring(lastUnderscoreIndex + 1)
                            : columnNameWithoutId;

                    // Check if this table exists in the project schema AND has a table (not just abstract/view)
                    var referencedResource = projectSchema.ResourceSchemas.Values.FirstOrDefault(rs =>
                        string.Equals(rs.ResourceName, potentialTableName, StringComparison.OrdinalIgnoreCase)
                    );

                    // Skip if this is an abstract resource (generates view, not table)
                    bool isAbstractResource = projectSchema.AbstractResources.ContainsKey(potentialTableName);

                    // Only create FK if the referenced resource exists, has a physical table, and is not abstract
                    if (
                        referencedResource != null
                        && referencedResource.FlatteningMetadata?.Table != null
                        && !isAbstractResource
                        && !naturalKeyReferences.Contains(potentialTableName)
                    )
                    {
                        naturalKeyReferences.Add(potentialTableName);
                        fkConstraintsToAdd.Add(
                            (
                                tableName,
                                finalSchemaName,
                                new
                                {
                                    constraintName = MssqlNamingHelper.MakeMssqlIdentifier(
                                        $"FK_{table.BaseName}_{potentialTableName}"
                                    ),
                                    column = MssqlNamingHelper.MakeMssqlIdentifier(column.ColumnName),
                                    parentTable = $"[{finalSchemaName}].[{MssqlNamingHelper.MakeMssqlIdentifier(DetermineTableName(potentialTableName, originalSchemaName, null, options))}]",
                                    parentColumn = "Id",
                                    cascade = false, // Use NO ACTION to prevent accidental data loss
                                }
                            )
                        );
                    }
                }
            }

            // Add FK constraints for cross-resource references (fromReferencePath)
            // Skip abstract resources (they generate views, not tables)
            foreach (var (columnName, referencedResource) in crossResourceReferences)
            {
                // Check if this resource exists and has a physical table
                var referencedResourceSchema = projectSchema?.ResourceSchemas.Values.FirstOrDefault(rs =>
                    string.Equals(rs.ResourceName, referencedResource, StringComparison.OrdinalIgnoreCase)
                );

                // Check if this is an abstract resource (generates view, not table)
                bool isAbstractResource =
                    projectSchema?.AbstractResources.ContainsKey(referencedResource) ?? false;

                // Only create FK if the referenced resource exists, has a physical table, and is not abstract
                if (
                    referencedResourceSchema != null
                    && referencedResourceSchema.FlatteningMetadata?.Table != null
                    && !isAbstractResource
                )
                {
                    fkConstraintsToAdd.Add(
                        (
                            tableName,
                            finalSchemaName,
                            new
                            {
                                constraintName = MssqlNamingHelper.MakeMssqlIdentifier(
                                    $"FK_{table.BaseName}_{referencedResource}"
                                ),
                                column = columnName,
                                parentTable = $"[{finalSchemaName}].[{DetermineTableName(referencedResource, originalSchemaName, null, options)}]",
                                parentColumn = "Id",
                                cascade = false,
                            }
                        )
                    );
                }
            }

            // Store Document FK constraint for later generation (FIXED: no double parentheses)
            // For child tables, use NO ACTION to avoid multiple cascade paths in SQL Server
            fkConstraintsToAdd.Add(
                (
                    tableName,
                    finalSchemaName,
                    new
                    {
                        constraintName = MssqlNamingHelper.MakeMssqlIdentifier(
                            $"FK_{table.BaseName}_Document"
                        ),
                        column = "Document_Id, Document_PartitionKey",
                        parentTable = $"[{finalSchemaName}].[{MssqlNamingHelper.MakeMssqlIdentifier("Document")}]",
                        parentColumn = "Id, DocumentPartitionKey",
                        cascade = isRootTable, // Only root tables cascade; child tables use NO ACTION
                    }
                )
            );

            // Unique constraints
            var uniqueConstraints = new List<object>();

            // For root tables: Natural key uniqueness
            if (isRootTable && naturalKeyColumns.Count > 0)
            {
                uniqueConstraints.Add(
                    new
                    {
                        constraintName = MssqlNamingHelper.MakeMssqlIdentifier(
                            $"UQ_{table.BaseName}_NaturalKey"
                        ),
                        columns = naturalKeyColumns,
                    }
                );
            }

            // For child tables: Parent FK + natural key columns
            if (!isRootTable)
            {
                var identityColumns = new List<string>
                {
                    MssqlNamingHelper.MakeMssqlIdentifier($"{parentTableName}_Id"),
                };
                identityColumns.AddRange(naturalKeyColumns);

                if (identityColumns.Count > 1) // Only if there are identifying columns beyond parent FK
                {
                    uniqueConstraints.Add(
                        new
                        {
                            constraintName = MssqlNamingHelper.MakeMssqlIdentifier(
                                $"UQ_{table.BaseName}_Identity"
                            ),
                            columns = identityColumns,
                        }
                    );
                }
            }

            // Index generation for foreign keys (critical for performance)
            var indexes = new List<object>();

            // 1. Index on parent FK (for child tables)
            if (parentTableName != null)
            {
                indexes.Add(
                    new
                    {
                        indexName = MssqlNamingHelper.MakeMssqlIdentifier(
                            $"IX_{table.BaseName}_{parentTableName}"
                        ),
                        tableName,
                        columns = new[] { MssqlNamingHelper.MakeMssqlIdentifier($"{parentTableName}_Id") },
                    }
                );
            }

            // 2. Indexes on cross-resource FKs
            foreach (var (columnName, referencedResource) in crossResourceReferences)
            {
                indexes.Add(
                    new
                    {
                        indexName = MssqlNamingHelper.MakeMssqlIdentifier(
                            $"IX_{table.BaseName}_{referencedResource}"
                        ),
                        tableName,
                        columns = new[] { MssqlNamingHelper.MakeMssqlIdentifier(columnName) },
                    }
                );
            }

            // 3. Index on Document FK
            indexes.Add(
                new
                {
                    indexName = MssqlNamingHelper.MakeMssqlIdentifier($"IX_{table.BaseName}_Document"),
                    tableName,
                    columns = new[]
                    {
                        MssqlNamingHelper.MakeMssqlIdentifier("Document_Id"),
                        MssqlNamingHelper.MakeMssqlIdentifier("Document_PartitionKey"),
                    },
                }
            );

            // Combine all columns for template
            var allColumns = new List<object>();
            allColumns.AddRange(columns);

            // Add audit columns if requested
            if (options.IncludeAuditColumns)
            {
                allColumns.AddRange(
                    new[]
                    {
                        new
                        {
                            name = "CreateDate",
                            type = "DATETIME2",
                            isRequired = true,
                        },
                        new
                        {
                            name = "LastModifiedDate",
                            type = "DATETIME2",
                            isRequired = true,
                        },
                        new
                        {
                            name = "ChangeVersion",
                            type = "BIGINT",
                            isRequired = true,
                        },
                    }
                );
            }

            // Add check constraint for discriminator if this table has a polymorphic reference and discriminator column
            object? discriminatorCheckConstraint = null;
            var hasPolymorphicRef = table.Columns.Any(c => c.IsPolymorphicReference);
            var discriminatorCol = table.Columns.FirstOrDefault(c => c.IsDiscriminator);
            if (hasPolymorphicRef && discriminatorCol != null)
            {
                // Collect allowed discriminator values from child tables
                var allowedValues = (table.ChildTables ?? new List<TableMetadata>())
                    .Where(ct => !string.IsNullOrEmpty(ct.DiscriminatorValue))
                    .Select(ct => ct.DiscriminatorValue!.Replace("'", "''"))
                    .Distinct()
                    .ToList();
                if (allowedValues.Count > 0)
                {
                    var constraintName = $"CK_{table.BaseName}_{discriminatorCol.ColumnName}_AllowedValues";
                    var allowedList = string.Join(", ", allowedValues.Select(v => $"'{v}'"));
                    var checkExpr = $"[{discriminatorCol.ColumnName}] IN ({allowedList})";
                    discriminatorCheckConstraint = new { constraintName, expression = checkExpr };
                }
            }

            // Prepare template data - ALL tables get Id and Document columns
            var data = new
            {
                schemaName = finalSchemaName,
                tableName,
                hasId = true, // All tables have Id
                id = "Id",
                hasDocumentColumns = true, // All tables have Document columns
                documentId = "Document_Id",
                documentPartitionKey = "Document_PartitionKey",
                columns = allColumns,
                fkColumns = new List<object>(), // NO FK constraints in this pass
                uniqueConstraints = options.GenerateNaturalKeyConstraints ? uniqueConstraints : [],
                indexes, // All tables get indexes (including Document index)
                checkConstraint = discriminatorCheckConstraint,
            };

            sb.AppendLine(template(data));

            // Recursively process child tables
            foreach (var childTable in table.ChildTables ?? System.Linq.Enumerable.Empty<TableMetadata>())
            {
                GenerateTableDdlWithoutForeignKeys(
                    childTable,
                    template,
                    sb,
                    table.BaseName,
                    null,
                    originalSchemaName,
                    options,
                    resourceSchema,
                    fkConstraintsToAdd,
                    projectSchema
                );
            }
        }

        private void GenerateTableDdl(
            TableMetadata table,
            HandlebarsTemplate<object, object> template,
            StringBuilder sb,
            string? parentTableName,
            List<string>? parentPkColumns,
            string originalSchemaName,
            DdlGenerationOptions options,
            ResourceSchema? resourceSchema = null,
            ProjectSchema? projectSchema = null
        )
        {
            var tableName = DetermineTableName(table.BaseName, originalSchemaName, resourceSchema, options);

            // For extension resources and descriptor resources using separate schemas, use the original schema as final schema
            var finalSchemaName = ShouldUseSeparateSchema(originalSchemaName, options, resourceSchema)
                ? originalSchemaName
                : options.ResolveSchemaName(null);

            var isRootTable = parentTableName == null;

            // Track cross-resource references for FK and index generation
            var crossResourceReferences = new List<(string columnName, string referencedResource)>();

            // Ensure collections are non-null to avoid CS8602 in downstream code
            table.Columns = table.Columns ?? new List<ColumnMetadata>();
            table.ChildTables = table.ChildTables ?? new List<TableMetadata>();

            // Ensure columns and child tables are safe for nullable metadata
            table.Columns = table.Columns ?? new List<ColumnMetadata>();
            table.ChildTables = table.ChildTables ?? new List<TableMetadata>();

            // Generate data columns (excluding parent references and system columns)
            var columns = table
                .Columns.Where(c => !c.IsParentReference) // Parent FKs handled separately
                .Select(c =>
                {
                    // Track cross-resource references
                    if (
                        !string.IsNullOrEmpty(c.FromReferencePath)
                        && c.ColumnName.EndsWith("_Id", StringComparison.OrdinalIgnoreCase)
                    )
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
                        isRequired = c.IsRequired,
                    };
                })
                .ToList();

            // Natural key columns for unique constraint
            var naturalKeyColumns = (table.Columns ?? new List<ColumnMetadata>())
                .Where(c => c.IsNaturalKey && !c.IsParentReference)
                .Select(c => c.ColumnName)
                .ToList();

            // Foreign key constraints
            var fkColumns = new List<object>();

            // Index generation for foreign keys (critical for performance)
            var indexes = new List<object>();

            // 1. FK to parent table (for child tables)
            if (parentTableName != null)
            {
                var parentFkColumn = $"{parentTableName}_Id";
                columns.Insert(
                    0,
                    new
                    {
                        name = parentFkColumn,
                        type = "BIGINT",
                        isRequired = true,
                    }
                );

                fkColumns.Add(
                    new
                    {
                        constraintName = MssqlNamingHelper.MakeMssqlIdentifier(
                            $"FK_{table.BaseName}_{parentTableName}"
                        ),
                        column = parentFkColumn,
                        parentTable = $"[{finalSchemaName}].[{DetermineTableName(parentTableName, originalSchemaName, resourceSchema, options)}]",
                        parentColumn = "[Id]", // Always reference surrogate key
                        cascade = true,
                    }
                );
            }

            // 2. FK to cross-resource references
            // Skip abstract resources (they generate views, not tables)
            foreach (var (columnName, referencedResource) in crossResourceReferences)
            {
                // Check if this resource exists and has a physical table
                var referencedResourceSchema = projectSchema?.ResourceSchemas.Values.FirstOrDefault(rs =>
                    string.Equals(rs.ResourceName, referencedResource, StringComparison.OrdinalIgnoreCase)
                );

                // Check if this is an abstract resource (generates view, not table)
                bool isAbstractResource =
                    projectSchema?.AbstractResources.ContainsKey(referencedResource) ?? false;

                // Only create FK if the referenced resource exists, has a physical table, and is not abstract
                if (
                    referencedResourceSchema != null
                    && referencedResourceSchema.FlatteningMetadata?.Table != null
                    && !isAbstractResource
                )
                {
                    fkColumns.Add(
                        new
                        {
                            constraintName = MssqlNamingHelper.MakeMssqlIdentifier(
                                $"FK_{table.BaseName}_{referencedResource}"
                            ),
                            column = columnName,
                            parentTable = $"[{finalSchemaName}].[{DetermineTableName(referencedResource, originalSchemaName, null, options)}]",
                            parentColumn = "[Id]",
                            cascade = false, // Use RESTRICT for cross-resource FKs to prevent accidental data loss
                        }
                    );
                }
            }

            // 2b. FK constraints for natural key columns that follow the pattern <TableName>_Id
            // (only if not already handled by cross-resource references)
            if (projectSchema != null)
            {
                var existingFkResources = crossResourceReferences
                    .Select(cr => cr.referencedResource)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var naturalKeyReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (
                    var column in (table.Columns ?? System.Linq.Enumerable.Empty<ColumnMetadata>()).Where(c =>
                        !c.IsParentReference
                        && c.ColumnName.EndsWith("_Id", StringComparison.OrdinalIgnoreCase)
                        && string.IsNullOrEmpty(c.FromReferencePath)
                    )
                )
                {
                    // Extract potential table name from column name (e.g., School_Id -> School)
                    // For prefixed columns like Participating_EducationOrganization_Id or BalanceSheet_BalanceSheetDimension_Id,
                    // extract the rightmost segment (the actual table name)
                    var columnNameWithoutId = column.ColumnName.Substring(0, column.ColumnName.Length - 3);

                    // Find the rightmost segment after the last underscore (this is the potential table name)
                    var lastUnderscoreIndex = columnNameWithoutId.LastIndexOf('_');
                    var potentialTableName =
                        lastUnderscoreIndex >= 0
                            ? columnNameWithoutId.Substring(lastUnderscoreIndex + 1)
                            : columnNameWithoutId;

                    // Check if this table exists in the project schema AND has a table (not just abstract/view)
                    var referencedResource = projectSchema.ResourceSchemas.Values.FirstOrDefault(rs =>
                        string.Equals(rs.ResourceName, potentialTableName, StringComparison.OrdinalIgnoreCase)
                    );

                    // Skip if this is an abstract resource (generates view, not table)
                    bool isAbstractResource = projectSchema.AbstractResources.ContainsKey(potentialTableName);

                    // Only create FK if the referenced resource exists, has a physical table, is not abstract, and not already handled
                    if (
                        referencedResource != null
                        && referencedResource.FlatteningMetadata?.Table != null
                        && !isAbstractResource
                        && !naturalKeyReferences.Contains(potentialTableName)
                        && !existingFkResources.Contains(potentialTableName)
                    )
                    {
                        naturalKeyReferences.Add(potentialTableName);
                        fkColumns.Add(
                            new
                            {
                                constraintName = MssqlNamingHelper.MakeMssqlIdentifier(
                                    $"FK_{table.BaseName}_{potentialTableName}"
                                ),
                                column = MssqlNamingHelper.MakeMssqlIdentifier(column.ColumnName),
                                parentTable = $"[{finalSchemaName}].[{MssqlNamingHelper.MakeMssqlIdentifier(DetermineTableName(potentialTableName, originalSchemaName, null, options))}]",
                                parentColumn = "[Id]",
                                cascade = false,
                            }
                        );
                    }
                }
            }

            // 3. FK to Document table (all tables) - FIXED: no double parentheses
            fkColumns.Add(
                new
                {
                    constraintName = MssqlNamingHelper.MakeMssqlIdentifier($"FK_{table.BaseName}_Document"),
                    column = "Document_Id, Document_PartitionKey",
                    parentTable = $"[{finalSchemaName}].[Document]",
                    parentColumn = "Id, DocumentPartitionKey",
                    cascade = true,
                }
            );

            // Unique constraints
            var uniqueConstraints = new List<object>();

            // For root tables: Natural key uniqueness
            if (isRootTable && naturalKeyColumns.Count > 0)
            {
                uniqueConstraints.Add(
                    new
                    {
                        constraintName = MssqlNamingHelper.MakeMssqlIdentifier(
                            $"UQ_{table.BaseName}_NaturalKey"
                        ),
                        columns = naturalKeyColumns,
                    }
                );
            }

            // Add check constraint for discriminator if this table has a polymorphic reference and discriminator column
            object? discriminatorCheckConstraint = null;
            var hasPolymorphicRef = (table.Columns ?? System.Linq.Enumerable.Empty<ColumnMetadata>()).Any(c =>
                c.IsPolymorphicReference
            );
            var discriminatorCol = (
                table.Columns ?? System.Linq.Enumerable.Empty<ColumnMetadata>()
            ).FirstOrDefault(c => c.IsDiscriminator);
            if (hasPolymorphicRef && discriminatorCol != null)
            {
                // Collect allowed discriminator values from child tables
                var allowedValues = (table.ChildTables ?? new List<TableMetadata>())
                    .Where(ct => !string.IsNullOrEmpty(ct.DiscriminatorValue))
                    .Select(ct => ct.DiscriminatorValue!.Replace("'", "''"))
                    .Distinct()
                    .ToList();
                if (allowedValues.Count > 0)
                {
                    var constraintName = $"CK_{table.BaseName}_{discriminatorCol.ColumnName}_AllowedValues";
                    var allowedList = string.Join(", ", allowedValues.Select(v => $"'{v}'"));
                    var checkExpr = $"[{discriminatorCol.ColumnName}] IN ({allowedList})";
                    discriminatorCheckConstraint = new { constraintName, expression = checkExpr };
                }
            }

            // Add audit columns if requested
            var allColumns = new List<object>(columns);
            if (options.IncludeAuditColumns)
            {
                allColumns.AddRange(
                    new[]
                    {
                        new
                        {
                            name = "CreateDate",
                            type = "DATETIME2(7)",
                            isRequired = true,
                        },
                        new
                        {
                            name = "LastModifiedDate",
                            type = "DATETIME2(7)",
                            isRequired = true,
                        },
                        new
                        {
                            name = "ChangeVersion",
                            type = "BIGINT",
                            isRequired = true,
                        },
                    }
                );
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
                indexes,
                checkConstraint = discriminatorCheckConstraint,
            };

            sb.AppendLine(template(data));

            // Recursively process child tables
            foreach (var childTable in table.ChildTables ?? System.Linq.Enumerable.Empty<TableMetadata>())
            {
                GenerateTableDdl(
                    childTable,
                    template,
                    sb,
                    table.BaseName,
                    null,
                    originalSchemaName,
                    options,
                    resourceSchema
                );
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
                "string" => MapStringType(column),

                // Boolean type
                "boolean" or "bool" => "BIT",

                // Date/time types
                "date" => "DATE",
                "datetime" => "DATETIME2(7)", // High precision as per spec
                "time" => "TIME",

                // Decimal types with precision/scale support
                "decimal" => MapDecimalType(column),

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
        /// Maps string column metadata to SQL Server NVARCHAR or NVARCHAR(MAX) types with proper length constraints.
        /// </summary>
        private static string MapStringType(ColumnMetadata column)
        {
            if (!string.IsNullOrEmpty(column.MaxLength))
            {
                var maxLength = int.Parse(column.MaxLength);
                // SQL Server NVARCHAR has a limit of 4000 characters; use NVARCHAR(MAX) for larger values
                return maxLength > 4000 ? "NVARCHAR(MAX)" : $"NVARCHAR({column.MaxLength})";
            }

            return "NVARCHAR(MAX)"; // Fallback for unlimited length
        }

        /// <summary>
        /// Maps decimal column metadata to SQL Server DECIMAL types with precision and scale from MetaEd metadata.
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

            // Handle dotted paths (e.g., "LearningStandardGrade.LearningStandard") by extracting the last segment
            var lastDotIndex = referencePath.LastIndexOf('.');
            var resourceName = lastDotIndex >= 0 ? referencePath.Substring(lastDotIndex + 1) : referencePath;

            // Remove "Reference" suffix if present
            if (resourceName.EndsWith("Reference", StringComparison.OrdinalIgnoreCase))
            {
                return resourceName.Substring(0, resourceName.Length - "Reference".Length);
            }

            // Otherwise, return the extracted resource name
            return resourceName;
        }

        /// <summary>
        /// Determines the original schema name for a resource without "dms" resolution.
        /// This is used for table name prefixing when UsePrefixedTableNames is true.
        /// </summary>
        /// <param name="projectSchema">The project schema containing the resource.</param>
        /// <param name="resourceSchema">The resource schema being processed.</param>
        /// <param name="options">DDL generation options containing schema mappings.</param>
        /// <returns>The original database schema name (before dms resolution).</returns>
        private string GetOriginalSchemaName(
            ProjectSchema projectSchema,
            ResourceSchema resourceSchema,
            DdlGenerationOptions options
        )
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
                        string.Equals(kvp.Key, extensionProject, StringComparison.OrdinalIgnoreCase)
                    );
                    if (!match.Equals(default(KeyValuePair<string, string>)))
                    {
                        return match.Value;
                    }
                    return extensionProject.ToLowerInvariant();
                }
                // Return mapped Extensions schema or default "extensions"
                return options.SchemaMapping.ContainsKey("Extensions")
                    ? options.SchemaMapping["Extensions"]
                    : "extensions";
            }

            // Use project name to determine original schema
            if (options.SchemaMapping.TryGetValue(projectSchema.ProjectName, out var projectSchemaMapping))
            {
                return projectSchemaMapping;
            }
            // Try case-insensitive match
            var projectMatch = options.SchemaMapping.FirstOrDefault(kvp =>
                string.Equals(kvp.Key, projectSchema.ProjectName, StringComparison.OrdinalIgnoreCase)
            );
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
        private string DetermineSchemaName(
            ProjectSchema projectSchema,
            ResourceSchema resourceSchema,
            DdlGenerationOptions options
        )
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
                        string.Equals(kvp.Key, extensionProject, StringComparison.OrdinalIgnoreCase)
                    );
                    if (!match.Equals(default(KeyValuePair<string, string>)))
                    {
                        return match.Value;
                    }
                    return extensionProject.ToLowerInvariant();
                }
                // Fall back to extensions schema
                return options.SchemaMapping.ContainsKey("Extensions")
                    ? options.SchemaMapping["Extensions"]
                    : "extensions";
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
                    string.Equals(kvp.Key, projectSchema.ProjectName, StringComparison.OrdinalIgnoreCase)
                );
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
            return resourceSchema.ResourceName.EndsWith("Descriptor", StringComparison.OrdinalIgnoreCase)
                || resourceSchema.ResourceName.EndsWith("Type", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Determines if a schema should use separate schemas instead of prefixed table names.
        /// </summary>
        /// <param name="originalSchemaName">The original schema name.</param>
        /// <param name="options">DDL generation options.</param>
        /// <param name="resourceSchema">The resource schema (optional, for extension detection).</param>
        /// <returns>True if should use separate schema; otherwise, false.</returns>
        private bool ShouldUseSeparateSchema(
            string originalSchemaName,
            DdlGenerationOptions options,
            ResourceSchema? resourceSchema = null
        )
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
                var match = System.Text.RegularExpressions.Regex.Match(
                    baseName,
                    @"^([A-Z]{2,5})(?=[A-Z][a-z])"
                );
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
        /// <param name="schemaName">The schema name this table belongs to.</param>
        /// <param name="resourceSchema">The resource schema context (for extension detection).</param>
        /// <param name="options">DDL generation options.</param>
        /// <returns>The final table name to use in DDL.</returns>
        private string DetermineTableName(
            string baseName,
            string schemaName,
            ResourceSchema? resourceSchema,
            DdlGenerationOptions options
        )
        {
            // Special case: if schemaName equals DescriptorSchema and it's different from DefaultSchema,
            // don't use prefixed table names (descriptor resources use separate schemas)
            if (schemaName == options.DescriptorSchema && schemaName != options.DefaultSchema)
            {
                return baseName;
            }

            // Extension resources that use separate schemas should use original table name
            if (resourceSchema?.FlatteningMetadata?.Table?.IsExtensionTable == true)
            {
                return baseName;
            }

            if (options.UsePrefixedTableNames && schemaName != options.DefaultSchema)
            {
                // Use schema name as prefix: edfi_School, tpdm_Student, etc.
                return $"{schemaName}_{baseName}";
            }

            return baseName;
        }

        /// <summary>
        /// Builds a full table reference including schema and proper table name.
        /// </summary>
        /// <param name="baseName">The base table name from metadata.</param>
        /// <param name="projectSchema">The project schema context.</param>
        /// <param name="resourceSchema">The resource schema context.</param>
        /// <param name="options">DDL generation options.</param>
        /// <returns>The full table reference ([schema].[tablename]).</returns>
        private string BuildTableReference(
            string baseName,
            ProjectSchema projectSchema,
            ResourceSchema? resourceSchema,
            DdlGenerationOptions options
        )
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

            // For extension resources and descriptor resources using separate schemas, use the original schema as final schema
            var finalSchemaName = ShouldUseSeparateSchema(schemaName, options)
                ? schemaName
                : options.ResolveSchemaName(null);

            return $"[{finalSchemaName}].[{tableName}]";
        }

        /// <summary>
        /// Generates a column alias from a column name.
        /// For columns ending with _Id, converts to PascalCase (e.g., School_Id -> SchoolId).
        /// For other columns, returns the column name as-is.
        /// </summary>
        private string GenerateColumnAlias(string columnName)
        {
            if (columnName.EndsWith("_Id", StringComparison.OrdinalIgnoreCase))
            {
                // Remove the "_Id" suffix and convert to PascalCase
                var baseName = columnName.Substring(0, columnName.Length - 3);
                return baseName + "Id";
            }
            return columnName;
        }

        /// <summary>
        /// Generates natural key resolution views for a table and its children.
        /// Views join to referenced tables to expose natural keys instead of surrogate keys.
        /// </summary>
        private void GenerateNaturalKeyViews(
            TableMetadata table,
            HandlebarsTemplate<object, object> template,
            StringBuilder sb,
            string? parentTableName,
            TableMetadata? parentTableMetadata,
            string originalSchemaName,
            DdlGenerationOptions options,
            ResourceSchema? resourceSchema,
            ProjectSchema projectSchema
        )
        {
            var tableName = MssqlNamingHelper.MakeMssqlIdentifier(
                DetermineTableName(table.BaseName, originalSchemaName, resourceSchema, options)
            );
            var viewName = $"{table.BaseName}_View";

            var finalSchemaName = ShouldUseSeparateSchema(originalSchemaName, options, resourceSchema)
                ? originalSchemaName
                : options.ResolveSchemaName(null);
            finalSchemaName = MssqlNamingHelper.MakeMssqlIdentifier(finalSchemaName);

            var selectColumns = new List<string>();
            var joins = new List<string>();

            // Always include base table columns
            selectColumns.Add("base.Id");
            selectColumns.Add("base.Document_Id");
            selectColumns.Add("base.Document_PartitionKey");

            // Track used aliases to avoid duplicates
            var usedAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Id",
                "Document_Id",
                "Document_PartitionKey",
                "CreateDate",
                "LastModifiedDate",
                "ChangeVersion",
            };

            // Track source columns (table.column) to avoid selecting the same column multiple times
            var usedSourceColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // If this is a child table, resolve parent's natural keys (exclude intermediate FK)
            if (parentTableName != null && parentTableMetadata != null)
            {
                var parentFkColumn = MssqlNamingHelper.MakeMssqlIdentifier($"{parentTableName}_Id");
                usedAliases.Add(parentFkColumn); // Track to avoid duplicate, but don't include in view

                // Join to parent table to resolve its natural keys
                var parentTableRef = MssqlNamingHelper.MakeMssqlIdentifier(
                    DetermineTableName(parentTableName, originalSchemaName, resourceSchema, options)
                );
                joins.Add(
                    $"\r\n    INNER JOIN [{finalSchemaName}].[{parentTableRef}] parent ON base.{parentFkColumn} = parent.Id"
                );

                // Add parent's natural key columns directly from parent TableMetadata
                foreach (
                    var parentNkCol in parentTableMetadata.Columns.Where(c =>
                        c.IsNaturalKey && !c.IsParentReference
                    )
                )
                {
                    var columnName = MssqlNamingHelper.MakeMssqlIdentifier(parentNkCol.ColumnName);
                    var aliasName = MssqlNamingHelper.MakeMssqlIdentifier(
                        GenerateColumnAlias(parentNkCol.ColumnName)
                    );
                    var sourceColumn = $"parent.{columnName}";

                    if (!usedSourceColumns.Contains(sourceColumn) && !usedAliases.Contains(aliasName))
                    {
                        selectColumns.Add($"{sourceColumn} AS {aliasName}");
                        usedAliases.Add(aliasName);
                        usedSourceColumns.Add(sourceColumn);
                    }
                    else
                    {
                        _logger.LogInformation(
                            "Column name collision detected in view for {Table}: {Alias} already exists, skipping parent natural key",
                            table.BaseName,
                            aliasName
                        );
                    }
                }

                // Recursively resolve parent's parent natural keys (grandparent and beyond)
                ResolveAncestorNaturalKeys(
                    parentTableMetadata,
                    projectSchema,
                    selectColumns,
                    joins,
                    "parent",
                    parentTableName,
                    finalSchemaName,
                    originalSchemaName,
                    options,
                    usedAliases,
                    usedSourceColumns,
                    resourceSchema,
                    1 // depth level starts at 1 for grandparent
                );

                // Recursively resolve parent's cross-resource references (only for resource-level tables)
                var parentResourceSchema = FindResourceSchemaByTableName(
                    projectSchema,
                    parentTableName,
                    options
                );
                if (parentResourceSchema != null)
                {
                    ResolveParentReferences(
                        parentTableMetadata,
                        projectSchema,
                        selectColumns,
                        joins,
                        "parent",
                        parentTableName,
                        finalSchemaName,
                        originalSchemaName,
                        options,
                        parentResourceSchema,
                        usedAliases,
                        usedSourceColumns
                    );
                }
            }

            // First pass: collect all FK columns that are part of cross-resource references
            var fkColumnsInReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in table.Columns ?? System.Linq.Enumerable.Empty<ColumnMetadata>())
            {
                if (
                    !string.IsNullOrEmpty(column.FromReferencePath)
                    && column.ColumnName.EndsWith("Id")
                    && !column.IsParentReference
                )
                {
                    fkColumnsInReferences.Add(column.ColumnName);
                }
            }

            // Process cross-resource references in current table
            var processedReferences = new HashSet<string>();
            foreach (var column in table.Columns ?? System.Linq.Enumerable.Empty<ColumnMetadata>())
            {
                if (
                    !string.IsNullOrEmpty(column.FromReferencePath)
                    && column.ColumnName.EndsWith("Id")
                    && !column.IsParentReference
                )
                {
                    var referencedResource = ResolveResourceNameFromPath(column.FromReferencePath);
                    if (
                        !string.IsNullOrEmpty(referencedResource)
                        && !processedReferences.Contains(referencedResource)
                    )
                    {
                        processedReferences.Add(referencedResource);

                        // Include all FK columns for this reference (handles composite keys)
                        foreach (
                            var fkCol in (
                                table.Columns ?? System.Linq.Enumerable.Empty<ColumnMetadata>()
                            ).Where(c =>
                                !string.IsNullOrEmpty(c.FromReferencePath)
                                && c.FromReferencePath == column.FromReferencePath
                                && c.ColumnName.EndsWith("Id")
                            )
                        )
                        {
                            var columnIdentifier = MssqlNamingHelper.MakeMssqlIdentifier(fkCol.ColumnName);
                            var aliasName = MssqlNamingHelper.MakeMssqlIdentifier(
                                GenerateColumnAlias(fkCol.ColumnName)
                            );
                            var sourceColumn = $"base.{columnIdentifier}";

                            if (!usedSourceColumns.Contains(sourceColumn) && !usedAliases.Contains(aliasName))
                            {
                                selectColumns.Add($"{sourceColumn} AS {aliasName}");
                                usedAliases.Add(aliasName);
                                usedSourceColumns.Add(sourceColumn);
                            }
                        }

                        // Find referenced resource schema
                        var referencedSchema = FindResourceSchemaByName(projectSchema, referencedResource);
                        if (referencedSchema?.FlatteningMetadata?.Table != null)
                        {
                            var refTable = referencedSchema.FlatteningMetadata.Table;
                            var refTableName = MssqlNamingHelper.MakeMssqlIdentifier(
                                DetermineTableName(
                                    refTable.BaseName,
                                    GetOriginalSchemaName(projectSchema, referencedSchema, options),
                                    referencedSchema,
                                    options
                                )
                            );
                            var refAlias = referencedResource.ToLower();

                            // Determine the join condition (use first FK column for single-column join)
                            var mainFkColumn = MssqlNamingHelper.MakeMssqlIdentifier(column.ColumnName);
                            joins.Add(
                                $"\r\n    INNER JOIN [{finalSchemaName}].[{refTableName}] {refAlias} ON base.{mainFkColumn} = {refAlias}.Id"
                            );

                            // Add natural key columns from referenced resource
                            foreach (
                                var refNkCol in refTable.Columns.Where(c =>
                                    c.IsNaturalKey && !c.IsParentReference
                                )
                            )
                            {
                                var columnName = MssqlNamingHelper.MakeMssqlIdentifier(refNkCol.ColumnName);
                                var aliasName = MssqlNamingHelper.MakeMssqlIdentifier(
                                    GenerateColumnAlias(refNkCol.ColumnName)
                                );
                                var sourceColumn = $"{refAlias}.{columnName}";

                                if (
                                    !usedSourceColumns.Contains(sourceColumn)
                                    && !usedAliases.Contains(aliasName)
                                )
                                {
                                    selectColumns.Add($"{sourceColumn} AS {aliasName}");
                                    usedAliases.Add(aliasName);
                                    usedSourceColumns.Add(sourceColumn);
                                }
                            }
                        }
                    }
                }
                else if (!column.IsParentReference && !fkColumnsInReferences.Contains(column.ColumnName))
                {
                    // Include regular data columns (skip FK columns already added above)
                    var columnIdentifier = MssqlNamingHelper.MakeMssqlIdentifier(column.ColumnName);
                    var sourceColumn = $"base.{columnIdentifier}";

                    if (!usedSourceColumns.Contains(sourceColumn) && !usedAliases.Contains(columnIdentifier))
                    {
                        selectColumns.Add($"{sourceColumn}");
                        usedAliases.Add(columnIdentifier);
                        usedSourceColumns.Add(sourceColumn);
                    }
                }
            }

            // Generate view with proper naming
            var sanitizedViewName = MssqlNamingHelper.MakeMssqlIdentifier($"{table.BaseName}_View");

            var viewData = new
            {
                viewName = sanitizedViewName,
                schemaName = finalSchemaName,
                baseTableName = tableName,
                selectColumns,
                joins,
            };

            sb.AppendLine(template(viewData));

            // Recursively process child tables
            foreach (var childTable in table.ChildTables ?? System.Linq.Enumerable.Empty<TableMetadata>())
            {
                GenerateNaturalKeyViews(
                    childTable,
                    template,
                    sb,
                    table.BaseName,
                    table, // Pass parent table metadata
                    originalSchemaName,
                    options,
                    resourceSchema,
                    projectSchema
                );
            }
        }

        /// <summary>
        /// Recursively resolves ancestor (grandparent and beyond) natural keys by chaining joins through the parent hierarchy.
        /// This ensures that natural keys from all ancestor levels propagate down to deeply nested child tables.
        /// </summary>
        private void ResolveAncestorNaturalKeys(
            TableMetadata parentTable,
            ProjectSchema projectSchema,
            List<string> selectColumns,
            List<string> joins,
            string currentAlias,
            string currentPrefix,
            string finalSchemaName,
            string originalSchemaName,
            DdlGenerationOptions options,
            HashSet<string> usedAliases,
            HashSet<string> usedSourceColumns,
            ResourceSchema? resourceSchema,
            int depth
        )
        {
            // Check if the parent table itself has a parent (making it a grandparent from the original child's perspective)
            var grandparentTableName = FindParentTableName(parentTable);
            if (grandparentTableName != null)
            {
                // Create alias for the grandparent join
                var ancestorAlias = $"ancestor_{depth}";
                var grandparentFkColumn = MssqlNamingHelper.MakeMssqlIdentifier($"{grandparentTableName}_Id");

                // Join to grandparent table through the parent
                var grandparentTableRef = MssqlNamingHelper.MakeMssqlIdentifier(
                    DetermineTableName(grandparentTableName, originalSchemaName, resourceSchema, options)
                );
                joins.Add(
                    $"\r\n    INNER JOIN [{finalSchemaName}].[{grandparentTableRef}] {ancestorAlias} ON {currentAlias}.{grandparentFkColumn} = {ancestorAlias}.Id"
                );

                // Try to find grandparent as a resource first, otherwise look for it as a child table
                var grandparentResourceSchema = FindResourceSchemaByTableName(
                    projectSchema,
                    grandparentTableName,
                    options
                );

                TableMetadata? grandparentTable = null;
                if (
                    grandparentResourceSchema != null
                    && grandparentResourceSchema.FlatteningMetadata?.Table != null
                )
                {
                    // Grandparent is a top-level resource
                    grandparentTable = grandparentResourceSchema.FlatteningMetadata.Table;
                }
                else
                {
                    // Grandparent is a child table - search for it in the resource hierarchy
                    grandparentTable = FindChildTableByName(
                        resourceSchema?.FlatteningMetadata?.Table,
                        grandparentTableName
                    );
                }

                if (grandparentTable != null)
                {
                    // Add grandparent's natural key columns without prefix
                    foreach (
                        var grandparentNkCol in grandparentTable.Columns.Where(c =>
                            c.IsNaturalKey && !c.IsParentReference
                        )
                    )
                    {
                        var columnName = MssqlNamingHelper.MakeMssqlIdentifier(grandparentNkCol.ColumnName);
                        var aliasName = MssqlNamingHelper.MakeMssqlIdentifier(
                            GenerateColumnAlias(grandparentNkCol.ColumnName)
                        );
                        var sourceColumn = $"{ancestorAlias}.{columnName}";

                        if (!usedSourceColumns.Contains(sourceColumn) && !usedAliases.Contains(aliasName))
                        {
                            selectColumns.Add($"{sourceColumn} AS {aliasName}");
                            usedAliases.Add(aliasName);
                            usedSourceColumns.Add(sourceColumn);
                        }
                        else
                        {
                            _logger.LogInformation(
                                "Column name collision detected in ancestor resolution for {Grandparent}: {Alias} already exists, skipping",
                                grandparentTableName,
                                aliasName
                            );
                        }
                    }

                    // Continue recursively up the chain for even higher ancestors
                    ResolveAncestorNaturalKeys(
                        grandparentTable,
                        projectSchema,
                        selectColumns,
                        joins,
                        ancestorAlias,
                        grandparentTableName, // Use grandparent name as new prefix for next level
                        finalSchemaName,
                        originalSchemaName,
                        options,
                        usedAliases,
                        usedSourceColumns,
                        grandparentResourceSchema ?? resourceSchema,
                        depth + 1
                    );
                }
            }
        }

        /// <summary>
        /// Recursively searches for a child table by name in the table hierarchy.
        /// </summary>
        private TableMetadata? FindChildTableByName(TableMetadata? table, string tableName)
        {
            if (table == null)
            {
                return null;
            }

            // Check direct children
            var child = table.ChildTables?.FirstOrDefault(c =>
                string.Equals(c.BaseName, tableName, StringComparison.Ordinal)
            );
            if (child != null)
            {
                return child;
            }

            // Recursively check grandchildren
            foreach (var childTable in table.ChildTables ?? System.Linq.Enumerable.Empty<TableMetadata>())
            {
                var found = FindChildTableByName(childTable, tableName);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        /// <summary>
        /// Finds the parent table name by looking for a column with IsParentReference = true.
        /// </summary>
        private string? FindParentTableName(TableMetadata table)
        {
            var parentRefColumn = table.Columns?.FirstOrDefault(c => c.IsParentReference);
            if (parentRefColumn != null && parentRefColumn.ColumnName.EndsWith("_Id"))
            {
                // Extract parent table name by removing the "_Id" suffix
                return parentRefColumn.ColumnName.Substring(0, parentRefColumn.ColumnName.Length - 3);
            }
            return null;
        }

        /// <summary>
        /// Resolves parent table's cross-resource references recursively.
        /// </summary>
        private void ResolveParentReferences(
            TableMetadata parentTable,
            ProjectSchema projectSchema,
            List<string> selectColumns,
            List<string> joins,
            string parentAlias,
            string parentPrefix,
            string finalSchemaName,
            string originalSchemaName,
            DdlGenerationOptions options,
            ResourceSchema parentResourceSchema,
            HashSet<string> usedAliases,
            HashSet<string> usedSourceColumns
        )
        {
            var processedRefs = new HashSet<string>();

            foreach (var column in parentTable.Columns ?? System.Linq.Enumerable.Empty<ColumnMetadata>())
            {
                if (
                    !string.IsNullOrEmpty(column.FromReferencePath)
                    && column.ColumnName.EndsWith("Id")
                    && !column.IsParentReference
                )
                {
                    var referencedResource = ResolveResourceNameFromPath(column.FromReferencePath);
                    if (
                        !string.IsNullOrEmpty(referencedResource)
                        && !processedRefs.Contains(referencedResource)
                    )
                    {
                        processedRefs.Add(referencedResource);

                        // Include parent's surrogate key column (only if not already included as natural key)
                        var baseColumnName = MssqlNamingHelper.MakeMssqlIdentifier(column.ColumnName);
                        var aliasName = MssqlNamingHelper.MakeMssqlIdentifier(
                            GenerateColumnAlias(column.ColumnName)
                        );
                        var sourceColumn = $"{parentAlias}.{baseColumnName}";

                        // Check if source column is already included
                        if (usedSourceColumns.Contains(sourceColumn))
                        {
                            _logger.LogInformation(
                                "Skipping cross-reference {Column} - source column {SourceColumn} already included",
                                column.ColumnName,
                                sourceColumn
                            );
                        }
                        else if (!usedAliases.Contains(aliasName))
                        {
                            selectColumns.Add($"{sourceColumn} AS {aliasName}");
                            usedAliases.Add(aliasName);
                            usedSourceColumns.Add(sourceColumn);
                        }

                        // Find referenced resource
                        var referencedSchema = FindResourceSchemaByName(projectSchema, referencedResource);
                        if (referencedSchema?.FlatteningMetadata?.Table != null)
                        {
                            var refTable = referencedSchema.FlatteningMetadata.Table;
                            var refTableName = MssqlNamingHelper.MakeMssqlIdentifier(
                                DetermineTableName(
                                    refTable.BaseName,
                                    GetOriginalSchemaName(projectSchema, referencedSchema, options),
                                    referencedSchema,
                                    options
                                )
                            );
                            var refAlias = $"{parentAlias}_{referencedResource.ToLower()}";

                            // Join to referenced table
                            joins.Add(
                                $"\r\n    INNER JOIN [{finalSchemaName}].[{refTableName}] {refAlias} ON {parentAlias}.{MssqlNamingHelper.MakeMssqlIdentifier(column.ColumnName)} = {refAlias}.Id"
                            );

                            // Add natural key columns
                            foreach (
                                var refNkCol in refTable.Columns.Where(c =>
                                    c.IsNaturalKey && !c.IsParentReference
                                )
                            )
                            {
                                var columnName = MssqlNamingHelper.MakeMssqlIdentifier(refNkCol.ColumnName);
                                var refAliasName = MssqlNamingHelper.MakeMssqlIdentifier(
                                    GenerateColumnAlias(refNkCol.ColumnName)
                                );
                                var refSourceColumn = $"{refAlias}.{columnName}";

                                if (
                                    !usedSourceColumns.Contains(refSourceColumn)
                                    && !usedAliases.Contains(refAliasName)
                                )
                                {
                                    selectColumns.Add($"{refSourceColumn} AS {refAliasName}");
                                    usedAliases.Add(refAliasName);
                                    usedSourceColumns.Add(refSourceColumn);
                                }
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Finds a resource schema by its resource name.
        /// </summary>
        private ResourceSchema? FindResourceSchemaByName(ProjectSchema projectSchema, string resourceName)
        {
            if (projectSchema.ResourceSchemas?.TryGetValue(resourceName, out var schema) == true)
            {
                return schema;
            }

            // Try case-insensitive match
            return projectSchema.ResourceSchemas?.Values.FirstOrDefault(rs =>
                string.Equals(rs.ResourceName, resourceName, StringComparison.OrdinalIgnoreCase)
            );
        }

        /// <summary>
        /// Finds a resource schema by its table base name.
        /// </summary>
        private ResourceSchema? FindResourceSchemaByTableName(
            ProjectSchema projectSchema,
            string tableName,
            DdlGenerationOptions options
        )
        {
            return projectSchema.ResourceSchemas?.Values.FirstOrDefault(rs =>
                rs.FlatteningMetadata?.Table != null
                && string.Equals(rs.FlatteningMetadata.Table.BaseName, tableName, StringComparison.Ordinal)
            );
        }

        /// <summary>
        /// Generates only descriptor foreign key constraints for SQL Server.
        /// </summary>
        public string? GenerateDescriptorForeignKeys(ApiSchema apiSchema, DdlGenerationOptions options)
        {
            if (apiSchema.ProjectSchema?.ResourceSchemas == null)
            {
                return null;
            }

            var sb = new StringBuilder();
            sb.AppendLine("-- =====================================================");
            sb.AppendLine("-- Descriptor Foreign Key Constraints Generator (SQL Server)");
            sb.AppendLine("-- This script scans all tables and creates FK constraints");
            sb.AppendLine("-- for descriptor columns only.");
            sb.AppendLine("-- =====================================================");
            sb.AppendLine();

            foreach (var kvp in apiSchema.ProjectSchema.ResourceSchemas)
            {
                var resourceSchema = kvp.Value;
                var table = resourceSchema.FlatteningMetadata?.Table;
                if (table == null)
                {
                    continue;
                }
                var originalSchemaName = GetOriginalSchemaName(
                    apiSchema.ProjectSchema,
                    resourceSchema,
                    options
                );
                var finalSchemaName = ShouldUseSeparateSchema(originalSchemaName, options, resourceSchema)
                    ? originalSchemaName
                    : options.ResolveSchemaName(null);
                var tableName = DetermineTableName(
                    table.BaseName,
                    originalSchemaName,
                    resourceSchema,
                    options
                );

                foreach (
                    var descriptorCol in table.Columns.Where(c =>
                        string.Equals(c.ColumnType, "descriptor", StringComparison.OrdinalIgnoreCase)
                    )
                )
                {
                    var descriptorTable = tableName.StartsWith("edfi_", StringComparison.OrdinalIgnoreCase)
                        ? $"[{finalSchemaName}].[edfi_Descriptor]"
                        : $"[{finalSchemaName}].[Descriptor]";
                    var constraintName = MssqlNamingHelper.MakeMssqlIdentifier(
                        $"FK_{table.BaseName}_{descriptorCol.ColumnName}_Descriptor"
                    );
                    sb.AppendLine(
                        $"ALTER TABLE [{finalSchemaName}].[{tableName}] ADD CONSTRAINT {constraintName}"
                    );
                    sb.AppendLine(
                        $"    FOREIGN KEY ([{descriptorCol.ColumnName}]) REFERENCES {descriptorTable}(Id);"
                    );
                    sb.AppendLine();
                }
            }

            return sb.ToString();
        }
    }
}
