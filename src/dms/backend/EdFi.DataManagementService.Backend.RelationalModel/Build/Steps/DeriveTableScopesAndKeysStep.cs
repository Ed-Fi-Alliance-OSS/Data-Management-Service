// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Backend.RelationalModel.Build.Steps;

/// <summary>
/// <para>
/// Derives the base set of relational table scopes from <c>resourceSchema.jsonSchemaForInsert</c>.
/// </para>
/// <para>
/// This step is responsible for creating the root table (<c>$</c>) and one child table per array path
/// (including nested arrays). Child table primary keys are a composite of the root document id,
/// ancestor ordinals, and the current <c>Ordinal</c> column.
/// </para>
/// <para>
/// Object schemas are treated as inline containers and do not create new tables. The <c>_ext</c> property
/// is intentionally skipped here and handled by extension-specific mapping steps.
/// </para>
/// <para>
/// The traversal is deterministic: property iteration is ordinal-sorted, and no logic depends on dictionary
/// enumeration order.
/// </para>
/// </summary>
public sealed class DeriveTableScopesAndKeysStep : IRelationalModelBuilderStep
{
    private const string ExtensionPropertyName = "_ext";
    private static readonly DbSchemaName _dmsSchemaName = new("dms");
    private static readonly DbTableName _documentTableName = new(_dmsSchemaName, "Document");
    private static readonly DbTableName _descriptorTableName = new(_dmsSchemaName, "Descriptor");

    /// <summary>
    /// Walks the JSON schema and populates <see cref="RelationalModelBuilderContext.ResourceModel"/> with the
    /// base table inventory (root + collection tables) and their key/foreign-key columns.
    /// </summary>
    /// <param name="context">The builder context containing schema inputs and the output model.</param>
    public void Execute(RelationalModelBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var projectName = RequireContextValue(context.ProjectName, nameof(context.ProjectName));
        var projectEndpointName = RequireContextValue(
            context.ProjectEndpointName,
            nameof(context.ProjectEndpointName)
        );
        var resourceName = RequireContextValue(context.ResourceName, nameof(context.ResourceName));

        var jsonSchemaForInsert =
            context.JsonSchemaForInsert
            ?? throw new InvalidOperationException(
                "JsonSchemaForInsert must be provided before deriving table scopes."
            );

        if (jsonSchemaForInsert is not JsonObject rootSchema)
        {
            throw new InvalidOperationException("Json schema root must be an object.");
        }

        JsonSchemaUnsupportedKeywordValidator.Validate(rootSchema, "$");

        var physicalSchema = RelationalNameConventions.NormalizeSchemaName(projectEndpointName);
        var rootBaseName =
            context.RootTableNameOverride ?? RelationalNameConventions.ToPascalCase(resourceName);
        var resourceLabel = $"{projectName}:{resourceName}";
        var storageKind = context.IsDescriptorResource
            ? ResourceStorageKind.SharedDescriptorTable
            : ResourceStorageKind.RelationalTables;

        if (context.IsDescriptorResource)
        {
            var descriptorRootTableScope = CreateDescriptorRootTable();
            var descriptorTables = new[] { descriptorRootTableScope.Table };

            context.ResourceModel = new RelationalResourceModel(
                new QualifiedResourceName(projectName, resourceName),
                physicalSchema,
                storageKind,
                descriptorRootTableScope.Table,
                descriptorTables,
                Array.Empty<DocumentReferenceBinding>(),
                Array.Empty<DescriptorEdgeSource>()
            );

            return;
        }

        var rootTableScope = CreateRootTable(
            physicalSchema,
            rootBaseName,
            context.OverrideCollisionDetector,
            resourceLabel
        );

        List<TableScope> tableScopes = [rootTableScope];

        DiscoverTables(
            rootSchema,
            [],
            [],
            [],
            rootTableScope,
            tableScopes,
            physicalSchema,
            rootBaseName,
            "$",
            context,
            resourceLabel
        );

        var tables = tableScopes.Select(scope => scope.Table).ToArray();

        context.ResourceModel = new RelationalResourceModel(
            new QualifiedResourceName(projectName, resourceName),
            physicalSchema,
            storageKind,
            rootTableScope.Table,
            tables,
            Array.Empty<DocumentReferenceBinding>(),
            Array.Empty<DescriptorEdgeSource>()
        );
    }

    /// <summary>
    /// Creates the resource root table, keyed by <c>DocumentId</c> and FK'd to <c>dms.Document</c>.
    /// </summary>
    private static TableScope CreateRootTable(
        DbSchemaName schema,
        string rootBaseName,
        OverrideCollisionDetector? collisionDetector,
        string resourceLabel
    )
    {
        var tableName = new DbTableName(schema, rootBaseName);
        var jsonScope = JsonPathExpressionCompiler.FromSegments([]);
        var key = BuildRootTableKey(tableName);

        var columns = BuildKeyColumns(key.Columns);

        var fkName = ConstraintNaming.BuildForeignKeyName(tableName, ConstraintNaming.DocumentToken);

        TableConstraint[] constraints =
        [
            new TableConstraint.ForeignKey(
                fkName,
                new[] { RelationalNameConventions.DocumentIdColumnName },
                _documentTableName,
                new[] { RelationalNameConventions.DocumentIdColumnName },
                OnDelete: ReferentialAction.Cascade
            ),
        ];

        var table = new DbTableModel(tableName, jsonScope, key, columns, constraints);

        collisionDetector?.RegisterTable(
            tableName,
            rootBaseName,
            BuildTableOrigin(tableName, resourceLabel, jsonScope)
        );
        collisionDetector?.RegisterColumn(
            tableName,
            RelationalNameConventions.DocumentIdColumnName,
            RelationalNameConventions.DocumentIdColumnName.Value,
            BuildColumnOrigin(
                tableName,
                RelationalNameConventions.DocumentIdColumnName,
                resourceLabel,
                jsonScope
            )
        );

        return new TableScope(table, Array.Empty<string>(), Array.Empty<string>());
    }

    /// <summary>
    /// Creates the shared descriptor table root (<c>dms.Descriptor</c>), keyed by <c>DocumentId</c> and
    /// FK'd to <c>dms.Document</c>.
    /// </summary>
    private static TableScope CreateDescriptorRootTable()
    {
        var jsonScope = JsonPathExpressionCompiler.FromSegments([]);
        var key = BuildRootTableKey(_descriptorTableName);

        var columns = BuildKeyColumns(key.Columns);

        var fkName = ConstraintNaming.BuildForeignKeyName(
            _descriptorTableName,
            ConstraintNaming.DocumentToken
        );

        TableConstraint[] constraints =
        [
            new TableConstraint.ForeignKey(
                fkName,
                new[] { RelationalNameConventions.DocumentIdColumnName },
                _documentTableName,
                new[] { RelationalNameConventions.DocumentIdColumnName },
                OnDelete: ReferentialAction.Cascade
            ),
        ];

        var table = new DbTableModel(_descriptorTableName, jsonScope, key, columns, constraints);

        return new TableScope(table, Array.Empty<string>(), Array.Empty<string>());
    }

    /// <summary>
    /// Recursively discovers child tables for array schemas, keeping objects inline and skipping scalars.
    /// </summary>
    private static void DiscoverTables(
        JsonObject schema,
        List<JsonPathSegment> scopeSegments,
        List<string> collectionBaseNames,
        List<string> defaultCollectionBaseNames,
        TableScope parentTable,
        List<TableScope> tables,
        DbSchemaName schemaName,
        string rootBaseName,
        string schemaPath,
        RelationalModelBuilderContext context,
        string resourceLabel
    )
    {
        var schemaKind = JsonSchemaTraversalConventions.DetermineSchemaKind(schema);

        switch (schemaKind)
        {
            case SchemaKind.Object:
                DiscoverObjectSchema(
                    schema,
                    scopeSegments,
                    collectionBaseNames,
                    defaultCollectionBaseNames,
                    parentTable,
                    tables,
                    schemaName,
                    rootBaseName,
                    schemaPath,
                    context,
                    resourceLabel
                );
                break;
            case SchemaKind.Array:
                DiscoverArraySchema(
                    schema,
                    scopeSegments,
                    collectionBaseNames,
                    defaultCollectionBaseNames,
                    parentTable,
                    tables,
                    schemaName,
                    rootBaseName,
                    schemaPath,
                    context,
                    resourceLabel
                );
                break;
            case SchemaKind.Scalar:
                break;
            default:
                throw new InvalidOperationException("Unknown schema kind while deriving table scopes.");
        }
    }

    /// <summary>
    /// Visits an object schema and recursively inspects its properties to discover array tables.
    /// </summary>
    private static void DiscoverObjectSchema(
        JsonObject schema,
        List<JsonPathSegment> scopeSegments,
        List<string> collectionBaseNames,
        List<string> defaultCollectionBaseNames,
        TableScope parentTable,
        List<TableScope> tables,
        DbSchemaName schemaName,
        string rootBaseName,
        string schemaPath,
        RelationalModelBuilderContext context,
        string resourceLabel
    )
    {
        if (!schema.TryGetPropertyValue("properties", out var propertiesNode) || propertiesNode is null)
        {
            return;
        }

        if (propertiesNode is not JsonObject propertiesObject)
        {
            throw new InvalidOperationException("Expected properties to be an object.");
        }

        foreach (var property in propertiesObject.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (string.Equals(property.Key, ExtensionPropertyName, StringComparison.Ordinal))
            {
                continue;
            }

            if (property.Value is not JsonObject propertySchema)
            {
                throw new InvalidOperationException(
                    $"Expected property schema to be an object at {property.Key}."
                );
            }

            var propertySchemaPath = $"{schemaPath}.properties.{property.Key}";

            JsonSchemaUnsupportedKeywordValidator.Validate(propertySchema, propertySchemaPath);

            List<JsonPathSegment> propertySegments =
            [
                .. scopeSegments,
                new JsonPathSegment.Property(property.Key),
            ];

            DiscoverTables(
                propertySchema,
                propertySegments,
                collectionBaseNames,
                defaultCollectionBaseNames,
                parentTable,
                tables,
                schemaName,
                rootBaseName,
                propertySchemaPath,
                context,
                resourceLabel
            );
        }
    }

    /// <summary>
    /// Visits an array schema, creates a child table for the array scope, and then descends into its item
    /// schema for nested collections.
    /// </summary>
    private static void DiscoverArraySchema(
        JsonObject schema,
        List<JsonPathSegment> scopeSegments,
        List<string> collectionBaseNames,
        List<string> defaultCollectionBaseNames,
        TableScope parentTable,
        List<TableScope> tables,
        DbSchemaName schemaName,
        string rootBaseName,
        string schemaPath,
        RelationalModelBuilderContext context,
        string resourceLabel
    )
    {
        if (!schema.TryGetPropertyValue("items", out var itemsNode) || itemsNode is null)
        {
            throw new InvalidOperationException("Array schema items must be an object.");
        }

        if (itemsNode is not JsonObject itemsSchema)
        {
            throw new InvalidOperationException("Array schema items must be an object.");
        }

        var itemsSchemaPath = $"{schemaPath}.items";

        JsonSchemaUnsupportedKeywordValidator.Validate(itemsSchema, itemsSchemaPath);

        if (scopeSegments.Count == 0 || scopeSegments[^1] is not JsonPathSegment.Property propertySegment)
        {
            throw new InvalidOperationException("Array schema must be rooted at a property segment.");
        }

        List<JsonPathSegment> arraySegments = [.. scopeSegments, new JsonPathSegment.AnyArrayElement()];
        var jsonScope = JsonPathExpressionCompiler.FromSegments(arraySegments);

        var defaultCollectionBaseName = RelationalNameConventions.ToCollectionBaseName(propertySegment.Name);
        var parentSuffix = string.Concat(collectionBaseNames);
        var hasOverride = context.TryGetNameOverride(
            jsonScope,
            NameOverrideKind.Collection,
            out var overrideName
        );

        var collectionBaseName = defaultCollectionBaseName;

        if (hasOverride)
        {
            var superclassBaseName = string.IsNullOrWhiteSpace(context.SuperclassResourceName)
                ? null
                : RelationalNameConventions.ToPascalCase(context.SuperclassResourceName);
            var includeSuperclass =
                !string.IsNullOrWhiteSpace(superclassBaseName)
                && !string.Equals(overrideName, defaultCollectionBaseName, StringComparison.Ordinal);
            var impliedPrefixes = RelationalModelSetSchemaHelpers.BuildCollectionOverridePrefixes(
                rootBaseName,
                parentSuffix,
                includeSuperclass ? superclassBaseName : null
            );

            collectionBaseName = RelationalModelSetSchemaHelpers.ResolveCollectionOverrideBaseName(
                overrideName,
                impliedPrefixes,
                jsonScope.Canonical,
                resourceLabel
            );
        }

        List<string> nextCollectionBaseNames = [.. collectionBaseNames, collectionBaseName];
        List<string> nextDefaultCollectionBaseNames =
        [
            .. defaultCollectionBaseNames,
            defaultCollectionBaseName,
        ];

        var childTableScope = CreateChildTable(
            schemaName,
            rootBaseName,
            parentTable,
            nextCollectionBaseNames,
            nextDefaultCollectionBaseNames,
            jsonScope,
            context.OverrideCollisionDetector,
            resourceLabel
        );

        tables.Add(childTableScope);

        DiscoverTables(
            itemsSchema,
            arraySegments,
            nextCollectionBaseNames,
            nextDefaultCollectionBaseNames,
            childTableScope,
            tables,
            schemaName,
            rootBaseName,
            itemsSchemaPath,
            context,
            resourceLabel
        );
    }

    /// <summary>
    /// Creates a child table for an array scope, including a composite PK and a cascading FK to its parent
    /// table.
    /// </summary>
    private static TableScope CreateChildTable(
        DbSchemaName schemaName,
        string rootBaseName,
        TableScope parentTable,
        IReadOnlyList<string> collectionBaseNames,
        IReadOnlyList<string> defaultCollectionBaseNames,
        JsonPathExpression jsonScope,
        OverrideCollisionDetector? collisionDetector,
        string resourceLabel
    )
    {
        var tableName = new DbTableName(
            schemaName,
            BuildCollectionTableName(rootBaseName, collectionBaseNames)
        );
        var key = BuildChildTableKey(tableName, rootBaseName, collectionBaseNames);
        var originalTableName = BuildCollectionTableName(rootBaseName, defaultCollectionBaseNames);
        var originalKey = BuildChildTableKey(
            new DbTableName(schemaName, originalTableName),
            rootBaseName,
            defaultCollectionBaseNames
        );
        var parentKeyColumns = BuildParentKeyColumnNames(rootBaseName, parentTable.CollectionBaseNames);

        var fkName = ConstraintNaming.BuildForeignKeyName(tableName, parentTable.Table.Table.Name);

        TableConstraint[] constraints =
        [
            new TableConstraint.ForeignKey(
                fkName,
                parentKeyColumns,
                parentTable.Table.Table,
                parentTable.Table.Key.Columns.Select(column => column.ColumnName).ToArray(),
                OnDelete: ReferentialAction.Cascade
            ),
        ];

        var columns = BuildKeyColumns(key.Columns);
        var table = new DbTableModel(tableName, jsonScope, key, columns, constraints);

        collisionDetector?.RegisterTable(
            tableName,
            originalTableName,
            BuildTableOrigin(tableName, resourceLabel, jsonScope)
        );

        for (var index = 0; index < key.Columns.Count; index++)
        {
            var finalColumn = key.Columns[index].ColumnName;
            var originalColumn = originalKey.Columns[index].ColumnName;

            collisionDetector?.RegisterColumn(
                tableName,
                finalColumn,
                originalColumn.Value,
                BuildColumnOrigin(tableName, finalColumn, resourceLabel, jsonScope)
            );
        }

        return new TableScope(table, collectionBaseNames, defaultCollectionBaseNames);
    }

    /// <summary>
    /// Builds the root-table PK columns.
    /// </summary>
    private static TableKey BuildRootTableKey(DbTableName tableName)
    {
        return new TableKey(
            ConstraintNaming.BuildPrimaryKeyName(tableName),
            new[]
            {
                new DbKeyColumn(RelationalNameConventions.DocumentIdColumnName, ColumnKind.ParentKeyPart),
            }
        );
    }

    /// <summary>
    /// Builds the child-table PK columns: root document id, ancestor ordinals, and the current
    /// <c>Ordinal</c>.
    /// </summary>
    private static TableKey BuildChildTableKey(
        DbTableName tableName,
        string rootBaseName,
        IReadOnlyList<string> collectionBaseNames
    )
    {
        List<DbKeyColumn> keyColumns =
        [
            new DbKeyColumn(
                RelationalNameConventions.RootDocumentIdColumnName(rootBaseName),
                ColumnKind.ParentKeyPart
            ),
        ];

        for (var index = 0; index < collectionBaseNames.Count - 1; index++)
        {
            keyColumns.Add(
                new DbKeyColumn(
                    RelationalNameConventions.ParentCollectionOrdinalColumnName(collectionBaseNames[index]),
                    ColumnKind.ParentKeyPart
                )
            );
        }

        keyColumns.Add(new DbKeyColumn(RelationalNameConventions.OrdinalColumnName, ColumnKind.Ordinal));

        return new TableKey(ConstraintNaming.BuildPrimaryKeyName(tableName), keyColumns.ToArray());
    }

    /// <summary>
    /// Builds the FK column list for a child table by projecting the parent's key parts onto the child.
    /// </summary>
    private static DbColumnName[] BuildParentKeyColumnNames(
        string rootBaseName,
        IReadOnlyList<string> parentCollectionBaseNames
    )
    {
        List<DbColumnName> keyColumns = [RelationalNameConventions.RootDocumentIdColumnName(rootBaseName)];

        foreach (var collectionBaseName in parentCollectionBaseNames)
        {
            keyColumns.Add(RelationalNameConventions.ParentCollectionOrdinalColumnName(collectionBaseName));
        }

        return keyColumns.ToArray();
    }

    /// <summary>
    /// Computes the physical child table name from the root name plus the concatenated collection base
    /// names.
    /// </summary>
    private static string BuildCollectionTableName(
        string rootBaseName,
        IReadOnlyList<string> collectionBaseNames
    )
    {
        if (collectionBaseNames.Count == 0)
        {
            return rootBaseName;
        }

        return rootBaseName + string.Concat(collectionBaseNames);
    }

    /// <summary>
    /// Seeds the table's column inventory with key columns, using scalar types appropriate for each key kind.
    /// </summary>
    private static DbColumnModel[] BuildKeyColumns(IReadOnlyList<DbKeyColumn> keyColumns)
    {
        DbColumnModel[] columns = new DbColumnModel[keyColumns.Count];

        for (var index = 0; index < keyColumns.Count; index++)
        {
            var keyColumn = keyColumns[index];
            var scalarType = ResolveKeyColumnScalarType(keyColumn);

            columns[index] = new DbColumnModel(
                keyColumn.ColumnName,
                keyColumn.Kind,
                scalarType,
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            );
        }

        return columns;
    }

    /// <summary>
    /// Resolves the scalar type used for key columns (document ids are <c>bigint</c>, ordinals are
    /// <c>int</c>).
    /// </summary>
    private static RelationalScalarType ResolveKeyColumnScalarType(DbKeyColumn keyColumn)
    {
        return keyColumn.Kind switch
        {
            ColumnKind.Ordinal => new RelationalScalarType(ScalarKind.Int32),
            ColumnKind.ParentKeyPart => IsDocumentIdColumn(keyColumn.ColumnName)
                ? new RelationalScalarType(ScalarKind.Int64)
                : new RelationalScalarType(ScalarKind.Int32),
            ColumnKind.DocumentFk => new RelationalScalarType(ScalarKind.Int64),
            _ => throw new InvalidOperationException(
                $"Unsupported key column kind '{keyColumn.Kind}' for {keyColumn.ColumnName.Value}."
            ),
        };
    }

    /// <summary>
    /// Identifies columns that represent a <c>DocumentId</c> (root <c>DocumentId</c> or <c>*_DocumentId</c>
    /// key parts).
    /// </summary>
    private static bool IsDocumentIdColumn(DbColumnName columnName)
    {
        if (
            string.Equals(
                columnName.Value,
                RelationalNameConventions.DocumentIdColumnName.Value,
                StringComparison.Ordinal
            )
        )
        {
            return true;
        }

        return columnName.Value.EndsWith("_DocumentId", StringComparison.Ordinal);
    }

    private static IdentifierCollisionOrigin BuildTableOrigin(
        DbTableName tableName,
        string resourceLabel,
        JsonPathExpression jsonScope
    )
    {
        var description = $"table {tableName.Schema.Value}.{tableName.Name}";

        return new IdentifierCollisionOrigin(description, resourceLabel, jsonScope.Canonical);
    }

    private static IdentifierCollisionOrigin BuildColumnOrigin(
        DbTableName tableName,
        DbColumnName columnName,
        string resourceLabel,
        JsonPathExpression jsonScope
    )
    {
        var description = $"column {tableName.Schema.Value}.{tableName.Name}.{columnName.Value}";

        return new IdentifierCollisionOrigin(description, resourceLabel, jsonScope.Canonical);
    }

    /// <summary>
    /// Ensures required string inputs are present on the context before derivation proceeds.
    /// </summary>
    private static string RequireContextValue(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{name} must be provided.");
        }

        return value;
    }

    /// <summary>
    /// Tracks the derived <see cref="DbTableModel"/> along with the collection-name chain used for key and FK
    /// column derivation.
    /// </summary>
    private sealed record TableScope(
        DbTableModel Table,
        IReadOnlyList<string> CollectionBaseNames,
        IReadOnlyList<string> DefaultCollectionBaseNames
    );
}
