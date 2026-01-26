// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Backend.RelationalModel;

public sealed class DeriveTableScopesAndKeysStep : IRelationalModelBuilderStep
{
    private const string ExtensionPropertyName = "_ext";
    private static readonly DbSchemaName DmsSchemaName = new("dms");
    private static readonly DbTableName DocumentTableName = new(DmsSchemaName, "Document");

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

        var physicalSchema = RelationalNameConventions.NormalizeSchemaName(projectEndpointName);
        var rootBaseName = RelationalNameConventions.ToPascalCase(resourceName);

        var rootTableScope = CreateRootTable(physicalSchema, rootBaseName);

        List<TableScope> tableScopes = [rootTableScope];

        DiscoverTables(rootSchema, [], [], rootTableScope, tableScopes, physicalSchema, rootBaseName);

        var tables = tableScopes.Select(scope => scope.Table).ToArray();

        context.ResourceModel = new RelationalResourceModel(
            new QualifiedResourceName(projectName, resourceName),
            physicalSchema,
            rootTableScope.Table,
            tables,
            tables,
            Array.Empty<DocumentReferenceBinding>(),
            Array.Empty<DescriptorEdgeSource>()
        );
    }

    private static TableScope CreateRootTable(DbSchemaName schema, string rootBaseName)
    {
        var tableName = new DbTableName(schema, rootBaseName);
        var jsonScope = JsonPathExpressionCompiler.FromSegments([]);

        var key = new TableKey(
            new[]
            {
                new DbKeyColumn(RelationalNameConventions.DocumentIdColumnName, ColumnKind.ParentKeyPart),
            }
        );

        var columns = BuildKeyColumns(key.Columns);

        var fkName = RelationalNameConventions.ForeignKeyName(
            tableName.Name,
            new[] { RelationalNameConventions.DocumentIdColumnName }
        );

        TableConstraint[] constraints =
        [
            new TableConstraint.ForeignKey(
                fkName,
                new[] { RelationalNameConventions.DocumentIdColumnName },
                DocumentTableName,
                new[] { RelationalNameConventions.DocumentIdColumnName },
                OnDelete: ReferentialAction.Cascade
            ),
        ];

        var table = new DbTableModel(tableName, jsonScope, key, columns, constraints);

        return new TableScope(table, Array.Empty<string>());
    }

    private static void DiscoverTables(
        JsonObject schema,
        List<JsonPathSegment> scopeSegments,
        List<string> collectionBaseNames,
        TableScope parentTable,
        List<TableScope> tables,
        DbSchemaName schemaName,
        string rootBaseName
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
                    parentTable,
                    tables,
                    schemaName,
                    rootBaseName
                );
                break;
            case SchemaKind.Array:
                DiscoverArraySchema(
                    schema,
                    scopeSegments,
                    collectionBaseNames,
                    parentTable,
                    tables,
                    schemaName,
                    rootBaseName
                );
                break;
            case SchemaKind.Scalar:
                break;
            default:
                throw new InvalidOperationException("Unknown schema kind while deriving table scopes.");
        }
    }

    private static void DiscoverObjectSchema(
        JsonObject schema,
        List<JsonPathSegment> scopeSegments,
        List<string> collectionBaseNames,
        TableScope parentTable,
        List<TableScope> tables,
        DbSchemaName schemaName,
        string rootBaseName
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

            List<JsonPathSegment> propertySegments =
            [
                .. scopeSegments,
                new JsonPathSegment.Property(property.Key),
            ];

            DiscoverTables(
                propertySchema,
                propertySegments,
                collectionBaseNames,
                parentTable,
                tables,
                schemaName,
                rootBaseName
            );
        }
    }

    private static void DiscoverArraySchema(
        JsonObject schema,
        List<JsonPathSegment> scopeSegments,
        List<string> collectionBaseNames,
        TableScope parentTable,
        List<TableScope> tables,
        DbSchemaName schemaName,
        string rootBaseName
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

        if (scopeSegments.Count == 0 || scopeSegments[^1] is not JsonPathSegment.Property propertySegment)
        {
            throw new InvalidOperationException("Array schema must be rooted at a property segment.");
        }

        var collectionBaseName = RelationalNameConventions.ToCollectionBaseName(propertySegment.Name);

        List<string> nextCollectionBaseNames = [.. collectionBaseNames, collectionBaseName];
        List<JsonPathSegment> arraySegments = [.. scopeSegments, new JsonPathSegment.AnyArrayElement()];
        var jsonScope = JsonPathExpressionCompiler.FromSegments(arraySegments);

        var childTableScope = CreateChildTable(
            schemaName,
            rootBaseName,
            parentTable,
            nextCollectionBaseNames,
            jsonScope
        );

        tables.Add(childTableScope);

        DiscoverTables(
            itemsSchema,
            arraySegments,
            nextCollectionBaseNames,
            childTableScope,
            tables,
            schemaName,
            rootBaseName
        );
    }

    private static TableScope CreateChildTable(
        DbSchemaName schemaName,
        string rootBaseName,
        TableScope parentTable,
        IReadOnlyList<string> collectionBaseNames,
        JsonPathExpression jsonScope
    )
    {
        var tableName = new DbTableName(
            schemaName,
            BuildCollectionTableName(rootBaseName, collectionBaseNames)
        );
        var key = BuildChildTableKey(rootBaseName, collectionBaseNames);
        var parentKeyColumns = BuildParentKeyColumnNames(rootBaseName, parentTable.CollectionBaseNames);

        var fkName = RelationalNameConventions.ForeignKeyName(tableName.Name, parentKeyColumns);

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

        return new TableScope(table, collectionBaseNames);
    }

    private static TableKey BuildChildTableKey(string rootBaseName, IReadOnlyList<string> collectionBaseNames)
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

        return new TableKey(keyColumns.ToArray());
    }

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

    private static string RequireContextValue(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{name} must be provided.");
        }

        return value;
    }

    private sealed record TableScope(DbTableModel Table, IReadOnlyList<string> CollectionBaseNames);
}
