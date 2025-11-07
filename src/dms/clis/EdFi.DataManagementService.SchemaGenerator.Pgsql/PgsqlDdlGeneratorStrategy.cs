// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using EdFi.DataManagementService.SchemaGenerator.Abstractions;
using HandlebarsDotNet;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.SchemaGenerator.Pgsql
{
    /// <summary>
    /// PostgreSQL DDL generation strategy implementation.
    /// </summary>
    public class PgsqlDdlGeneratorStrategy(ILogger<PgsqlDdlGeneratorStrategy> _logger) : IDdlGeneratorStrategy
    {
        /// <summary>
        /// Generates PostgreSQL DDL scripts for the given ApiSchema metadata.
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
                "pgsql-table-idempotent.hbs"
            );
            if (!File.Exists(templatePath))
            {
                templatePath = Path.Combine(
                    Directory.GetCurrentDirectory(),
                    "Templates",
                    "pgsql-table-idempotent.hbs"
                );
            }

            var templateContent = File.ReadAllText(templatePath);
            var template = Handlebars.Compile(templateContent);

            var sb = new StringBuilder();

            // Generate schema creation statements for all unique schemas
            var usedSchemas = new HashSet<string>();
            foreach (
                var kvp in apiSchema.ProjectSchema.ResourceSchemas ?? new Dictionary<string, ResourceSchema>()
            )
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

            // PASS 1: Generate tables WITHOUT foreign key constraints
            var fkConstraintsToAdd = new List<(string tableName, string schemaName, object fkConstraint)>();

            foreach (
                var kvp in apiSchema.ProjectSchema.ResourceSchemas ?? new Dictionary<string, ResourceSchema>()
            )
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
            }

            // PASS 2: Add foreign key constraints via ALTER TABLE statements
            if (options.GenerateForeignKeyConstraints && fkConstraintsToAdd.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("-- Foreign Key Constraints");
                sb.AppendLine();

                foreach (var (tableName, schemaName, fkConstraint) in fkConstraintsToAdd)
                {
                    var constraint = (dynamic)fkConstraint;
                    var constraintName = PgsqlNamingHelper.MakePgsqlIdentifier(constraint.constraintName);
                    sb.AppendLine($"DO $$");
                    sb.AppendLine($"BEGIN");
                    sb.AppendLine(
                        $"        IF NOT EXISTS (\n            SELECT 1 FROM pg_constraint WHERE lower(conname) = lower('{constraintName}')\n        ) THEN"
                    );
                    sb.AppendLine(
                        $"            ALTER TABLE {schemaName}.{tableName} ADD CONSTRAINT {constraintName}"
                    );
                    sb.AppendLine($"                FOREIGN KEY ({constraint.column})");
                    sb.AppendLine(
                        $"                REFERENCES {constraint.parentTable}({constraint.parentColumn}){(constraint.cascade ? " ON DELETE CASCADE" : "")};"
                    );
                    sb.AppendLine($"        END IF;");
                    sb.AppendLine($"END$$;");
                    sb.AppendLine();
                }
            }

            // PASS 3: Generate natural key resolution views
            if (!options.SkipNaturalKeyViews)
            {
                var naturalKeyViewTemplatePath = Path.Combine(
                    AppContext.BaseDirectory,
                    "Templates",
                    "pgsql-natural-key-view.hbs"
                );
                if (!File.Exists(naturalKeyViewTemplatePath))
                {
                    naturalKeyViewTemplatePath = Path.Combine(
                        Directory.GetCurrentDirectory(),
                        "Templates",
                        "pgsql-natural-key-view.hbs"
                    );
                }
                var naturalKeyViewTemplateContent = File.ReadAllText(naturalKeyViewTemplatePath);
                var naturalKeyViewTemplate = Handlebars.Compile(naturalKeyViewTemplateContent);

                bool headerAdded = false;

                foreach (
                    var kvp in apiSchema.ProjectSchema.ResourceSchemas
                        ?? new Dictionary<string, ResourceSchema>()
                )
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
                    "pgsql-union-view.hbs"
                );
                if (!File.Exists(unionViewTemplatePath))
                {
                    unionViewTemplatePath = Path.Combine(
                        Directory.GetCurrentDirectory(),
                        "Templates",
                        "pgsql-union-view.hbs"
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
                                    var tableName = DetermineUnquotedTableName(
                                        table.BaseName,
                                        originalSchemaName,
                                        subclassSchema,
                                        options
                                    );
                                    var finalSchemaName = options.ResolveSchemaName(null);
                                    var tableRef = $"{finalSchemaName}.{tableName}";
                                    var discriminator = table.DiscriminatorValue ?? subclassType;

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

                            foreach (var (tableRef, discriminator) in subclassInfos)
                            {
                                // For each subclass, attempt to include the subclass column marked as the superclass identity
                                // as the natural key for the union view (e.g. SchoolId AS EducationOrganizationId)
                                var colsList = new List<string> { "Id" };

                                // Attempt to find a matching subclass schema by tableRef suffix (unquoted table name)
                                string? unquotedTableName = tableRef.Split('.').Last();
                                ResourceSchema? matchingSchema = null;
                                foreach (var st in subclassTypes)
                                {
                                    if (
                                        apiSchema.ProjectSchema.ResourceSchemas?.TryGetValue(st, out var ss)
                                            == true
                                        && ss.FlatteningMetadata?.Table != null
                                    )
                                    {
                                        var candidateName = DetermineUnquotedTableName(
                                            ss.FlatteningMetadata.Table.BaseName,
                                            GetOriginalSchemaName(apiSchema.ProjectSchema, ss, options),
                                            ss,
                                            options
                                        );
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
                                        matchingSchema?.FlatteningMetadata?.Table?.Columns
                                        ?? System.Linq.Enumerable.Empty<ColumnMetadata>()
                                    ).FirstOrDefault(c => c.IsSuperclassIdentity);

                                    if (supCol != null && !string.IsNullOrEmpty(supCol.ColumnName))
                                    {
                                        // Include the subclass column aliased to the abstract natural key name (unquoted)
                                        colsList.Add($"{supCol.ColumnName} AS {viewNaturalKeyName}");
                                    }
                                }

                                // Add remaining common columns (excluding any superclass identity already added)
                                var matchingCols =
                                    matchingSchema?.FlatteningMetadata?.Table?.Columns
                                    ?? System.Linq.Enumerable.Empty<ColumnMetadata>();
                                var toAdd = commonColumns.Where(c =>
                                    matchingSchema == null
                                    || !matchingCols.Any(col =>
                                        col.IsSuperclassIdentity
                                        && string.Equals(
                                            col.ColumnName,
                                            c,
                                            StringComparison.OrdinalIgnoreCase
                                        )
                                    )
                                );
                                colsList.AddRange(toAdd);

                                var documentColumns = "Document_Id, Document_PartitionKey";
                                if (options.IncludeAuditColumns)
                                {
                                    documentColumns += ", CreateDate, LastModifiedDate, ChangeVersion";
                                }

                                var selectCols = string.Join(", ", colsList);
                                selectStatements.Add(
                                    $"SELECT {selectCols}, '{discriminator}' AS Discriminator, {documentColumns} FROM {tableRef}"
                                );
                            }

                            // Only generate view if we have select statements
                            if (selectStatements.Any())
                            {
                                // Apply schema prefix to view name if using prefixed table names
                                var finalViewName = DetermineUnquotedTableName(
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
                foreach (
                    var resourceSchema in (
                        apiSchema.ProjectSchema.ResourceSchemas ?? new Dictionary<string, ResourceSchema>()
                    ).Values
                )
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
                            var viewName = PgsqlNamingHelper.MakePgsqlIdentifier(table.BaseName);
                            var selectStatements = new List<string>();

                            // Get the natural key columns from the parent table (these should be common across all child tables)
                            var naturalKeyColumns = (
                                table.Columns ?? System.Linq.Enumerable.Empty<ColumnMetadata>()
                            )
                                .Where(c => c.IsNaturalKey)
                                .Select(c => PgsqlNamingHelper.MakePgsqlIdentifier(c.ColumnName))
                                .ToList();

                            foreach (
                                var childTable in (table.ChildTables ?? new List<TableMetadata>()).Where(ct =>
                                    !string.IsNullOrEmpty(ct.DiscriminatorValue)
                                )
                            )
                            {
                                // Include ALL columns per specification: Id + all data columns + Document columns
                                var columns = new List<string> { "Id" };
                                columns.AddRange(
                                    (childTable.Columns ?? new List<ColumnMetadata>()).Select(c =>
                                        PgsqlNamingHelper.MakePgsqlIdentifier(c.ColumnName)
                                    )
                                );

                                var selectCols = string.Join(", ", columns);
                                var discriminatorValue = childTable.DiscriminatorValue;
                                var originalSchemaName = GetOriginalSchemaName(
                                    apiSchema.ProjectSchema,
                                    resourceSchema,
                                    options
                                );
                                var tableName = DetermineUnquotedTableName(
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
                                selectStatements.Add(
                                    $"SELECT {selectCols}, '{discriminatorValue}' AS Discriminator, {documentColumns} FROM {tableRef}"
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
            var tableName = DetermineTableName(table.BaseName, originalSchemaName, resourceSchema, options);

            // For extension resources and descriptor resources using separate schemas, use the original schema as final schema
            var finalSchemaName = ShouldUseSeparateSchema(originalSchemaName, options, resourceSchema)
                ? originalSchemaName
                : options.ResolveSchemaName(null);

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
                        name = PgsqlNamingHelper.MakePgsqlIdentifier(c.ColumnName),
                        type = MapColumnType(c),
                        isRequired = c.IsRequired,
                    };
                })
                .ToList();

            // Natural key columns for unique constraint
            var naturalKeyColumns = table
                .Columns.Where(c => c.IsNaturalKey && !c.IsParentReference)
                .Select(c => PgsqlNamingHelper.MakePgsqlIdentifier(c.ColumnName))
                .ToList();

            // Add parent FK column for child tables
            if (parentTableName != null)
            {
                var parentFkColumn = PgsqlNamingHelper.MakePgsqlIdentifier($"{parentTableName}_Id");
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
                            constraintName = $"FK_{table.BaseName}_{parentTableName}",
                            column = parentFkColumn,
                            parentTable = $"{finalSchemaName}.{DetermineTableName(parentTableName, originalSchemaName, resourceSchema, options)}",
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
                // If table name starts with edfi_ (case-insensitive), use edfi_descriptor, else descriptor
                var descriptorTable = tableName.StartsWith("edfi_", StringComparison.OrdinalIgnoreCase)
                    ? "dms.edfi_descriptor"
                    : "dms.descriptor";
                fkConstraintsToAdd.Add(
                    (
                        tableName,
                        finalSchemaName,
                        new
                        {
                            constraintName = PgsqlNamingHelper.MakePgsqlIdentifier(
                                $"FK_{table.BaseName}_{descriptorCol.ColumnName}_Descriptor"
                            ),
                            column = PgsqlNamingHelper.MakePgsqlIdentifier(descriptorCol.ColumnName),
                            parentTable = descriptorTable,
                            parentColumn = "Id",
                            cascade = false,
                        }
                    )
                );
            }

            // Add FK constraints for natural key columns that follow the pattern <TableName>_Id
            // These are natural keys that reference other tables (similar to view joins)
            if (projectSchema != null)
            {
                var naturalKeyReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (
                    var column in table.Columns.Where(c =>
                        c.IsNaturalKey
                        && !c.IsParentReference
                        && c.ColumnName.EndsWith("_Id", StringComparison.OrdinalIgnoreCase)
                        && string.IsNullOrEmpty(c.FromReferencePath)
                    )
                ) // Only if not already handled by cross-resource refs
                {
                    // Extract potential table name from column name (e.g., School_Id -> School)
                    var potentialTableName = column.ColumnName.Substring(0, column.ColumnName.Length - 3);

                    // Check if this table exists in the project schema
                    var referencedTableExists = projectSchema.ResourceSchemas.Values.Any(rs =>
                        string.Equals(rs.ResourceName, potentialTableName, StringComparison.OrdinalIgnoreCase)
                    );

                    if (referencedTableExists && !naturalKeyReferences.Contains(potentialTableName))
                    {
                        naturalKeyReferences.Add(potentialTableName);
                        fkConstraintsToAdd.Add(
                            (
                                tableName,
                                finalSchemaName,
                                new
                                {
                                    constraintName = PgsqlNamingHelper.MakePgsqlIdentifier(
                                        $"FK_{table.BaseName}_{potentialTableName}"
                                    ),
                                    column = PgsqlNamingHelper.MakePgsqlIdentifier(column.ColumnName),
                                    parentTable = $"{finalSchemaName}.{DetermineTableName(potentialTableName, originalSchemaName, null, options)}",
                                    parentColumn = "Id",
                                    cascade = false, // Use RESTRICT to prevent accidental data loss
                                }
                            )
                        );
                    }
                }
            }

            // Store Document FK constraint for later generation (FIXED: no double parentheses)
            fkConstraintsToAdd.Add(
                (
                    tableName,
                    finalSchemaName,
                    new
                    {
                        constraintName = PgsqlNamingHelper.MakePgsqlIdentifier(
                            $"FK_{table.BaseName}_Document"
                        ),
                        column = "Document_Id, Document_PartitionKey",
                        parentTable = $"{finalSchemaName}.Document",
                        parentColumn = "Id, DocumentPartitionKey",
                        cascade = true,
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
                        constraintName = PgsqlNamingHelper.MakePgsqlIdentifier(
                            $"UQ_{table.BaseName}_NaturalKey"
                        ),
                        columns = naturalKeyColumns,
                    }
                );
            }

            // Parent FK column for child tables (if applicable)
            var parentFk =
                parentTableName != null
                    ? new
                    {
                        column = PgsqlNamingHelper.MakePgsqlIdentifier($"{parentTableName}_Id"),
                        parentTable = $"{finalSchemaName}.{PgsqlNamingHelper.MakePgsqlIdentifier(parentTableName)}",
                    }
                    : null;

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
                            type = "TIMESTAMP",
                            isRequired = true,
                        },
                        new
                        {
                            name = "LastModifiedDate",
                            type = "TIMESTAMP",
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
                    var constraintName =
                        $"ck_{table.BaseName.ToLowerInvariant()}_{discriminatorCol.ColumnName.ToLowerInvariant()}_allowedvalues";
                    var allowedList = string.Join(", ", allowedValues.Select(v => $"'{v}'"));
                    var checkExpr =
                        $"{PgsqlNamingHelper.MakePgsqlIdentifier(discriminatorCol.ColumnName)} IN ({allowedList})";
                    discriminatorCheckConstraint = new { constraintName, expression = checkExpr };
                }
            }

            // Prepare template data - ALL tables get Id and Document columns
            var tableData = new
            {
                schemaName = finalSchemaName,
                tableName = PgsqlNamingHelper.MakePgsqlIdentifier(tableName),
                hasId = true, // All tables have Id
                id = "Id",
                hasDocumentColumns = true, // All tables have Document columns
                documentId = "Document_Id",
                documentPartitionKey = "Document_PartitionKey",
                columns = allColumns,
                uniqueConstraints,
                parentFk,
                indexes = new[]
                {
                    new
                    {
                        indexName = PgsqlNamingHelper.MakePgsqlIdentifier($"IX_{table.BaseName}_Document"),
                        tableName = PgsqlNamingHelper.MakePgsqlIdentifier(tableName),
                        columns = new[] { "Document_Id", "Document_PartitionKey" },
                    },
                },
                checkConstraint = discriminatorCheckConstraint,
            };

            // Generate table DDL
            sb.AppendLine(template(tableData));

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

            // Generate data columns (excluding parent references and system columns)
            var columns = table
                .Columns.Where(c => !c.IsParentReference) // Parent FKs handled separately
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
                        isRequired = c.IsRequired,
                    };
                })
                .ToList();

            // Natural key columns for unique constraint
            var naturalKeyColumns = table
                .Columns.Where(c => c.IsNaturalKey && !c.IsParentReference)
                .Select(c => PgsqlNamingHelper.MakePgsqlIdentifier(c.ColumnName))
                .ToList();

            // Foreign key constraints
            var fkColumns = new List<object>();

            // 1. FK to parent table (for child tables)
            if (parentTableName != null)
            {
                var parentFkColumn = PgsqlNamingHelper.MakePgsqlIdentifier($"{parentTableName}_Id");
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
                        constraintName = PgsqlNamingHelper.MakePgsqlIdentifier(
                            $"FK_{table.BaseName}_{parentTableName}"
                        ),
                        column = parentFkColumn,
                        parentTable = $"{finalSchemaName}.{DetermineTableName(parentTableName, originalSchemaName, resourceSchema, options)}",
                        parentColumn = "Id", // Always reference surrogate key
                        cascade = true,
                    }
                );
            }

            // 2. FK to cross-resource references
            foreach (var (columnName, referencedResource) in crossResourceReferences)
            {
                fkColumns.Add(
                    new
                    {
                        constraintName = PgsqlNamingHelper.MakePgsqlIdentifier(
                            $"FK_{table.BaseName}_{referencedResource}"
                        ),
                        column = PgsqlNamingHelper.MakePgsqlIdentifier(columnName),
                        parentTable = $"{finalSchemaName}.{DetermineTableName(referencedResource, originalSchemaName, null, options)}",
                        parentColumn = "Id",
                        cascade = false, // Use RESTRICT for cross-resource FKs to prevent accidental data loss
                    }
                );
            }

            // 2.5. FK for natural key columns that follow the pattern <TableName>_Id
            // These are natural keys that reference other tables (similar to view joins)
            if (projectSchema != null)
            {
                var naturalKeyReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (
                    var column in table.Columns.Where(c =>
                        c.IsNaturalKey
                        && !c.IsParentReference
                        && c.ColumnName.EndsWith("_Id", StringComparison.OrdinalIgnoreCase)
                    )
                )
                {
                    // Extract potential table name from column name (e.g., School_Id -> School)
                    var potentialTableName = column.ColumnName.Substring(0, column.ColumnName.Length - 3);

                    // Check if this table exists in the project schema
                    var referencedTableExists = projectSchema.ResourceSchemas.Values.Any(rs =>
                        string.Equals(rs.ResourceName, potentialTableName, StringComparison.OrdinalIgnoreCase)
                    );

                    if (referencedTableExists && !naturalKeyReferences.Contains(potentialTableName))
                    {
                        naturalKeyReferences.Add(potentialTableName);
                        fkColumns.Add(
                            new
                            {
                                constraintName = PgsqlNamingHelper.MakePgsqlIdentifier(
                                    $"FK_{table.BaseName}_{potentialTableName}"
                                ),
                                column = PgsqlNamingHelper.MakePgsqlIdentifier(column.ColumnName),
                                parentTable = $"{finalSchemaName}.{DetermineTableName(potentialTableName, originalSchemaName, null, options)}",
                                parentColumn = "Id",
                                cascade = false, // Use RESTRICT to prevent accidental data loss
                            }
                        );
                    }
                }
            }

            // 3. FK to Document table (all tables) - FIXED: no double parentheses
            fkColumns.Add(
                new
                {
                    constraintName = PgsqlNamingHelper.MakePgsqlIdentifier($"FK_{table.BaseName}_Document"),
                    column = "Document_Id, Document_PartitionKey",
                    parentTable = $"{finalSchemaName}.Document",
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
                        constraintName = PgsqlNamingHelper.MakePgsqlIdentifier(
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
                    PgsqlNamingHelper.MakePgsqlIdentifier($"{parentTableName}_Id"),
                };
                identityColumns.AddRange(naturalKeyColumns);

                if (identityColumns.Count > 1) // Only if there are identifying columns beyond parent FK
                {
                    uniqueConstraints.Add(
                        new
                        {
                            constraintName = PgsqlNamingHelper.MakePgsqlIdentifier(
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
                        indexName = PgsqlNamingHelper.MakePgsqlIdentifier(
                            $"IX_{table.BaseName}_{parentTableName}"
                        ),
                        tableName,
                        columns = new[] { PgsqlNamingHelper.MakePgsqlIdentifier($"{parentTableName}_Id") },
                    }
                );
            }

            // 2. Indexes on cross-resource FKs
            foreach (var (columnName, referencedResource) in crossResourceReferences)
            {
                indexes.Add(
                    new
                    {
                        indexName = PgsqlNamingHelper.MakePgsqlIdentifier(
                            $"IX_{table.BaseName}_{referencedResource}"
                        ),
                        tableName,
                        columns = new[] { PgsqlNamingHelper.MakePgsqlIdentifier(columnName) },
                    }
                );
            }

            // 3. Index on Document FK (composite index for performance)
            indexes.Add(
                new
                {
                    indexName = PgsqlNamingHelper.MakePgsqlIdentifier($"IX_{table.BaseName}_Document"),
                    tableName,
                    columns = new[] { "Document_Id", "Document_PartitionKey" },
                }
            );

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
                            type = "TIMESTAMP",
                            isRequired = true,
                        },
                        new
                        {
                            name = "LastModifiedDate",
                            type = "TIMESTAMP",
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
                    var constraintName =
                        $"ck_{table.BaseName.ToLowerInvariant()}_{discriminatorCol.ColumnName.ToLowerInvariant()}_allowedvalues";
                    var allowedList = string.Join(", ", allowedValues.Select(v => $"'{v}'"));
                    var checkExpr =
                        $"{PgsqlNamingHelper.MakePgsqlIdentifier(discriminatorCol.ColumnName)} IN ({allowedList})";
                    discriminatorCheckConstraint = new { constraintName, expression = checkExpr };
                }
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
                    resourceSchema,
                    projectSchema
                );
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
            if (options.SchemaMapping.TryGetValue(projectSchema.ProjectName, out var projectSchema1))
            {
                return projectSchema1;
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
        /// <param name="originalSchemaName">The original schema name before prefix resolution.</param>
        /// <param name="resourceSchema">The resource schema context (for extension detection).</param>
        /// <param name="options">DDL generation options.</param>
        /// <returns>The final table name to use in DDL.</returns>
        private string DetermineTableName(
            string baseName,
            string originalSchemaName,
            ResourceSchema? resourceSchema,
            DdlGenerationOptions options
        )
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
        /// Determines the final table name without applying quoting. This is used for union view
        /// generation where we prefer unquoted identifiers to match existing conventions.
        /// </summary>
        private string DetermineUnquotedTableName(
            string baseName,
            string originalSchemaName,
            ResourceSchema? resourceSchema,
            DdlGenerationOptions options
        )
        {
            // Mirror DetermineTableName logic but do not apply PgsqlNamingHelper quoting
            if (originalSchemaName == options.DescriptorSchema && originalSchemaName != options.DefaultSchema)
            {
                return baseName;
            }

            if (resourceSchema?.FlatteningMetadata?.Table?.IsExtensionTable == true)
            {
                return baseName;
            }

            if (options.UsePrefixedTableNames && originalSchemaName != options.DefaultSchema)
            {
                return $"{originalSchemaName}_{baseName}";
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
        /// <returns>The full table reference (schema.tablename).</returns>
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
            var finalSchemaName = options.ResolveSchemaName(null); // Always use the resolved schema (dms when prefixed)

            return $"{finalSchemaName}.{tableName}";
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
            var tableName = DetermineTableName(table.BaseName, originalSchemaName, resourceSchema, options);
            var viewName = $"{table.BaseName}_View";

            var finalSchemaName = ShouldUseSeparateSchema(originalSchemaName, options, resourceSchema)
                ? originalSchemaName
                : options.ResolveSchemaName(null);

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
                var parentFkColumn = PgsqlNamingHelper.MakePgsqlIdentifier($"{parentTableName}_Id");
                usedAliases.Add(parentFkColumn); // Track to avoid duplicate, but don't include in view

                // Join to parent table to resolve its natural keys
                var parentTableRef = DetermineTableName(
                    parentTableName,
                    originalSchemaName,
                    resourceSchema,
                    options
                );
                joins.Add(
                    $"\r\n    INNER JOIN {finalSchemaName}.{parentTableRef} parent ON base.{parentFkColumn} = parent.Id"
                );

                // Add parent's natural key columns directly from parent TableMetadata
                foreach (
                    var parentNkCol in parentTableMetadata.Columns.Where(c =>
                        c.IsNaturalKey && !c.IsParentReference
                    )
                )
                {
                    var columnName = PgsqlNamingHelper.MakePgsqlIdentifier(parentNkCol.ColumnName);
                    var aliasName = PgsqlNamingHelper.MakePgsqlIdentifier(
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
                    usedAliases,
                    usedSourceColumns,
                    "parent",
                    parentTableName,
                    finalSchemaName,
                    originalSchemaName,
                    options,
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
                        usedAliases,
                        usedSourceColumns,
                        "parent",
                        parentTableName,
                        finalSchemaName,
                        originalSchemaName,
                        options,
                        parentResourceSchema
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
                            var columnIdentifier = PgsqlNamingHelper.MakePgsqlIdentifier(fkCol.ColumnName);
                            var aliasName = PgsqlNamingHelper.MakePgsqlIdentifier(
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
                            var refTableName = DetermineTableName(
                                refTable.BaseName,
                                GetOriginalSchemaName(projectSchema, referencedSchema, options),
                                referencedSchema,
                                options
                            );
                            var refAlias = referencedResource.ToLower();

                            // Determine the join condition (use first FK column for single-column join)
                            var mainFkColumn = PgsqlNamingHelper.MakePgsqlIdentifier(column.ColumnName);
                            joins.Add(
                                $"\r\n    INNER JOIN {finalSchemaName}.{refTableName} {refAlias} ON base.{mainFkColumn} = {refAlias}.Id"
                            );

                            // Add natural key columns from referenced resource
                            foreach (
                                var refNkCol in refTable.Columns.Where(c =>
                                    c.IsNaturalKey && !c.IsParentReference
                                )
                            )
                            {
                                var columnName = PgsqlNamingHelper.MakePgsqlIdentifier(refNkCol.ColumnName);
                                var aliasName = PgsqlNamingHelper.MakePgsqlIdentifier(
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
                    var columnIdentifier = PgsqlNamingHelper.MakePgsqlIdentifier(column.ColumnName);
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
            var sanitizedViewName = PgsqlNamingHelper.MakePgsqlIdentifier($"{table.BaseName}_View");

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
            HashSet<string> usedAliases,
            HashSet<string> usedSourceColumns,
            string currentAlias,
            string currentPrefix,
            string finalSchemaName,
            string originalSchemaName,
            DdlGenerationOptions options,
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
                var grandparentFkColumn = PgsqlNamingHelper.MakePgsqlIdentifier($"{grandparentTableName}_Id");

                // Join to grandparent table through the parent
                var grandparentTableRef = DetermineTableName(
                    grandparentTableName,
                    originalSchemaName,
                    resourceSchema,
                    options
                );
                joins.Add(
                    $"\r\n    INNER JOIN {finalSchemaName}.{grandparentTableRef} {ancestorAlias} ON {currentAlias}.{grandparentFkColumn} = {ancestorAlias}.Id"
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
                        var columnName = PgsqlNamingHelper.MakePgsqlIdentifier(grandparentNkCol.ColumnName);
                        var aliasName = PgsqlNamingHelper.MakePgsqlIdentifier(
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
                        usedAliases,
                        usedSourceColumns,
                        ancestorAlias,
                        grandparentTableName, // Use grandparent name as new prefix for next level
                        finalSchemaName,
                        originalSchemaName,
                        options,
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
            HashSet<string> usedAliases,
            HashSet<string> usedSourceColumns,
            string parentAlias,
            string parentPrefix,
            string finalSchemaName,
            string originalSchemaName,
            DdlGenerationOptions options,
            ResourceSchema parentResourceSchema
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
                        var baseColumnName = PgsqlNamingHelper.MakePgsqlIdentifier(column.ColumnName);
                        var aliasName = PgsqlNamingHelper.MakePgsqlIdentifier(
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
                            var refTableName = DetermineTableName(
                                refTable.BaseName,
                                GetOriginalSchemaName(projectSchema, referencedSchema, options),
                                referencedSchema,
                                options
                            );
                            var refAlias = $"{parentAlias}_{referencedResource.ToLower()}";

                            // Join to referenced table
                            joins.Add(
                                $"\r\n    INNER JOIN {finalSchemaName}.{refTableName} {refAlias} ON {parentAlias}.{PgsqlNamingHelper.MakePgsqlIdentifier(column.ColumnName)} = {refAlias}.Id"
                            );

                            // Add natural key columns
                            foreach (
                                var refNkCol in refTable.Columns.Where(c =>
                                    c.IsNaturalKey && !c.IsParentReference
                                )
                            )
                            {
                                var refColumnName = PgsqlNamingHelper.MakePgsqlIdentifier(
                                    refNkCol.ColumnName
                                );
                                var refAliasName = PgsqlNamingHelper.MakePgsqlIdentifier(
                                    GenerateColumnAlias(refNkCol.ColumnName)
                                );
                                var refSourceColumn = $"{refAlias}.{refColumnName}";

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
        /// Generates only descriptor foreign key constraints for PostgreSQL.
        /// </summary>
        public string? GenerateDescriptorForeignKeys(ApiSchema apiSchema, DdlGenerationOptions options)
        {
            if (apiSchema.ProjectSchema?.ResourceSchemas == null)
            {
                return null;
            }

            var sb = new StringBuilder();
            sb.AppendLine("-- =====================================================");
            sb.AppendLine("-- Descriptor Foreign Key Constraints Generator (PostgreSQL)");
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
                        ? "dms.edfi_descriptor"
                        : "dms.descriptor";
                    var constraintName = PgsqlNamingHelper.MakePgsqlIdentifier(
                        $"FK_{table.BaseName}_{descriptorCol.ColumnName}_Descriptor"
                    );
                    sb.AppendLine($"DO $$");
                    sb.AppendLine($"BEGIN");
                    sb.AppendLine(
                        $"        IF NOT EXISTS (\n            SELECT 1 FROM pg_constraint WHERE lower(conname) = lower('{constraintName}')\n        ) THEN"
                    );
                    sb.AppendLine(
                        $"            ALTER TABLE {finalSchemaName}.{tableName} ADD CONSTRAINT {constraintName}"
                    );
                    sb.AppendLine(
                        $"                FOREIGN KEY ({PgsqlNamingHelper.MakePgsqlIdentifier(descriptorCol.ColumnName)})"
                    );
                    sb.AppendLine($"                REFERENCES {descriptorTable}(Id);");
                    sb.AppendLine($"        END IF;");
                    sb.AppendLine($"END$$;");
                    sb.AppendLine();
                }
            }

            return sb.ToString();
        }
    }
}
