// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Backend.RelationalModel;

public sealed class DeriveColumnsAndDescriptorEdgesStep : IRelationalModelBuilderStep
{
    private const string ExtensionPropertyName = "_ext";
    private static readonly DbSchemaName DmsSchemaName = new("dms");
    private static readonly DbTableName DescriptorTableName = new(DmsSchemaName, "Descriptor");

    public void Execute(RelationalModelBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var resourceModel =
            context.ResourceModel
            ?? throw new InvalidOperationException(
                "Resource model must be provided before deriving columns."
            );

        var jsonSchemaForInsert =
            context.JsonSchemaForInsert
            ?? throw new InvalidOperationException(
                "JsonSchemaForInsert must be provided before deriving columns."
            );

        if (jsonSchemaForInsert is not JsonObject rootSchema)
        {
            throw new InvalidOperationException("Json schema root must be an object.");
        }

        var tableBuilders = resourceModel
            .TablesInReadDependencyOrder.Select(table => new TableBuilder(table))
            .ToDictionary(builder => builder.Definition.JsonScope.Canonical, StringComparer.Ordinal);

        if (!tableBuilders.TryGetValue("$", out var rootTable))
        {
            throw new InvalidOperationException("Root table scope '$' was not found.");
        }

        var identityPaths = new HashSet<string>(
            context.IdentityJsonPaths.Select(path => path.Canonical),
            StringComparer.Ordinal
        );
        HashSet<string> usedDescriptorPaths = new(StringComparer.Ordinal);
        List<DescriptorEdgeSource> descriptorEdgeSources = [];

        WalkSchema(
            rootSchema,
            rootTable,
            [],
            [],
            hasOptionalAncestor: false,
            context,
            tableBuilders,
            identityPaths,
            usedDescriptorPaths,
            descriptorEdgeSources
        );

        EnsureAllDescriptorPathsUsed(context, usedDescriptorPaths);

        var updatedTables = resourceModel
            .TablesInReadDependencyOrder.Select(table => tableBuilders[table.JsonScope.Canonical].Build())
            .ToArray();

        var updatedRoot = tableBuilders[resourceModel.Root.JsonScope.Canonical].Build();

        context.ResourceModel = resourceModel with
        {
            Root = updatedRoot,
            TablesInReadDependencyOrder = updatedTables,
            TablesInWriteDependencyOrder = updatedTables,
            DescriptorEdgeSources = descriptorEdgeSources.ToArray(),
        };
    }

    private static void WalkSchema(
        JsonObject schema,
        TableBuilder tableBuilder,
        List<JsonPathSegment> pathSegments,
        List<string> columnSegments,
        bool hasOptionalAncestor,
        RelationalModelBuilderContext context,
        IReadOnlyDictionary<string, TableBuilder> tableBuilders,
        HashSet<string> identityPaths,
        HashSet<string> usedDescriptorPaths,
        List<DescriptorEdgeSource> descriptorEdgeSources
    )
    {
        var currentPath = JsonPathExpressionCompiler.FromSegments(pathSegments).Canonical;
        var schemaKind = DetermineSchemaKind(schema);

        switch (schemaKind)
        {
            case SchemaKind.Object:
                WalkObjectSchema(
                    schema,
                    tableBuilder,
                    pathSegments,
                    columnSegments,
                    hasOptionalAncestor,
                    context,
                    tableBuilders,
                    identityPaths,
                    usedDescriptorPaths,
                    descriptorEdgeSources
                );
                break;
            case SchemaKind.Array:
                WalkArraySchema(
                    schema,
                    pathSegments,
                    context,
                    tableBuilders,
                    identityPaths,
                    usedDescriptorPaths,
                    descriptorEdgeSources
                );
                break;
            case SchemaKind.Scalar:
                throw new InvalidOperationException($"Unexpected scalar schema at {currentPath}.");
            default:
                throw new InvalidOperationException("Unknown schema kind while deriving columns.");
        }
    }

    private static void WalkObjectSchema(
        JsonObject schema,
        TableBuilder tableBuilder,
        List<JsonPathSegment> pathSegments,
        List<string> columnSegments,
        bool hasOptionalAncestor,
        RelationalModelBuilderContext context,
        IReadOnlyDictionary<string, TableBuilder> tableBuilders,
        HashSet<string> identityPaths,
        HashSet<string> usedDescriptorPaths,
        List<DescriptorEdgeSource> descriptorEdgeSources
    )
    {
        if (!schema.TryGetPropertyValue("properties", out var propertiesNode) || propertiesNode is null)
        {
            return;
        }

        if (propertiesNode is not JsonObject propertiesObject)
        {
            var scopePath = JsonPathExpressionCompiler.FromSegments(pathSegments).Canonical;

            throw new InvalidOperationException($"Expected properties to be an object at {scopePath}.");
        }

        var requiredProperties = GetRequiredProperties(schema, pathSegments);

        foreach (var property in propertiesObject.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (string.Equals(property.Key, ExtensionPropertyName, StringComparison.Ordinal))
            {
                continue;
            }

            if (property.Value is not JsonObject propertySchema)
            {
                var propertyPathForError = BuildPropertyPath(pathSegments, property.Key);

                throw new InvalidOperationException(
                    $"Expected property schema to be an object at {propertyPathForError}."
                );
            }

            var propertyPathSegments = BuildPropertySegments(pathSegments, property.Key);
            var propertyColumnSegments = BuildPropertyColumnSegments(columnSegments, property.Key);
            var propertyPath = JsonPathExpressionCompiler.FromSegments(propertyPathSegments);
            var isRequired = requiredProperties.Contains(property.Key);
            var isXNullable = IsXNullable(propertySchema, propertyPath.Canonical);
            var isOptional = !isRequired;
            var isNullable = hasOptionalAncestor || isOptional || isXNullable;
            var nextHasOptionalAncestor = hasOptionalAncestor || isOptional || isXNullable;

            var schemaKind = DetermineSchemaKind(propertySchema);
            switch (schemaKind)
            {
                case SchemaKind.Object:
                    WalkSchema(
                        propertySchema,
                        tableBuilder,
                        propertyPathSegments,
                        propertyColumnSegments,
                        nextHasOptionalAncestor,
                        context,
                        tableBuilders,
                        identityPaths,
                        usedDescriptorPaths,
                        descriptorEdgeSources
                    );
                    break;
                case SchemaKind.Array:
                    WalkArraySchema(
                        propertySchema,
                        propertyPathSegments,
                        context,
                        tableBuilders,
                        identityPaths,
                        usedDescriptorPaths,
                        descriptorEdgeSources
                    );
                    break;
                case SchemaKind.Scalar:
                    AddScalarOrDescriptorColumn(
                        tableBuilder,
                        propertySchema,
                        propertyColumnSegments,
                        propertyPath,
                        isNullable,
                        context,
                        identityPaths,
                        usedDescriptorPaths,
                        descriptorEdgeSources
                    );
                    break;
                default:
                    throw new InvalidOperationException($"Unknown schema kind at {propertyPath.Canonical}.");
            }
        }
    }

    private static void WalkArraySchema(
        JsonObject schema,
        List<JsonPathSegment> propertySegments,
        RelationalModelBuilderContext context,
        IReadOnlyDictionary<string, TableBuilder> tableBuilders,
        HashSet<string> identityPaths,
        HashSet<string> usedDescriptorPaths,
        List<DescriptorEdgeSource> descriptorEdgeSources
    )
    {
        var arrayPath = JsonPathExpressionCompiler.FromSegments(propertySegments).Canonical;

        if (!schema.TryGetPropertyValue("items", out var itemsNode) || itemsNode is null)
        {
            throw new InvalidOperationException($"Array schema items must be an object at {arrayPath}.");
        }

        if (itemsNode is not JsonObject itemsSchema)
        {
            throw new InvalidOperationException($"Array schema items must be an object at {arrayPath}.");
        }

        List<JsonPathSegment> arraySegments = [.. propertySegments, new JsonPathSegment.AnyArrayElement()];

        var arrayScope = JsonPathExpressionCompiler.FromSegments(arraySegments).Canonical;

        if (!tableBuilders.TryGetValue(arrayScope, out var childTable))
        {
            throw new InvalidOperationException($"Child table scope '{arrayScope}' was not found.");
        }

        WalkSchema(
            itemsSchema,
            childTable,
            arraySegments,
            [],
            hasOptionalAncestor: false,
            context,
            tableBuilders,
            identityPaths,
            usedDescriptorPaths,
            descriptorEdgeSources
        );
    }

    private static void AddScalarOrDescriptorColumn(
        TableBuilder tableBuilder,
        JsonObject schema,
        IReadOnlyList<string> columnSegments,
        JsonPathExpression sourcePath,
        bool isNullable,
        RelationalModelBuilderContext context,
        HashSet<string> identityPaths,
        HashSet<string> usedDescriptorPaths,
        List<DescriptorEdgeSource> descriptorEdgeSources
    )
    {
        if (context.TryGetDescriptorPath(sourcePath, out var descriptorPathInfo))
        {
            var descriptorBaseName = BuildColumnBaseName(columnSegments);
            var columnName = RelationalNameConventions.DescriptorIdColumnName(descriptorBaseName);
            var column = new DbColumnModel(
                columnName,
                ColumnKind.DescriptorFk,
                new RelationalScalarType(ScalarKind.Int64),
                isNullable,
                descriptorPathInfo.DescriptorValuePath,
                descriptorPathInfo.DescriptorResource
            );

            tableBuilder.AddColumn(column);
            tableBuilder.AddConstraint(
                new TableConstraint.ForeignKey(
                    BuildForeignKeyName(tableBuilder.Definition.Table.Name, new[] { columnName }),
                    new[] { columnName },
                    DescriptorTableName,
                    new[] { RelationalNameConventions.DocumentIdColumnName }
                )
            );

            var isIdentityComponent = identityPaths.Contains(sourcePath.Canonical);
            descriptorEdgeSources.Add(
                new DescriptorEdgeSource(
                    isIdentityComponent,
                    descriptorPathInfo.DescriptorValuePath,
                    tableBuilder.Definition.Table,
                    columnName,
                    descriptorPathInfo.DescriptorResource
                )
            );

            usedDescriptorPaths.Add(sourcePath.Canonical);
            return;
        }

        var scalarType = ResolveScalarType(schema, sourcePath, context);
        var scalarColumn = new DbColumnModel(
            new DbColumnName(BuildColumnBaseName(columnSegments)),
            ColumnKind.Scalar,
            scalarType,
            isNullable,
            sourcePath,
            null
        );

        tableBuilder.AddColumn(scalarColumn);
    }

    private static RelationalScalarType ResolveScalarType(
        JsonObject schema,
        JsonPathExpression sourcePath,
        RelationalModelBuilderContext context
    )
    {
        var schemaType = GetSchemaType(schema, sourcePath.Canonical);

        return schemaType switch
        {
            "string" => ResolveStringType(schema, sourcePath),
            "integer" => ResolveIntegerType(schema, sourcePath),
            "number" => ResolveDecimalType(sourcePath, context),
            "boolean" => new RelationalScalarType(ScalarKind.Boolean),
            _ => throw new InvalidOperationException(
                $"Unsupported scalar type '{schemaType}' at {sourcePath.Canonical}."
            ),
        };
    }

    private static RelationalScalarType ResolveStringType(JsonObject schema, JsonPathExpression sourcePath)
    {
        var format = GetOptionalString(schema, "format", sourcePath.Canonical);

        if (!string.IsNullOrWhiteSpace(format))
        {
            return format switch
            {
                "date" => new RelationalScalarType(ScalarKind.Date),
                "date-time" => new RelationalScalarType(ScalarKind.DateTime),
                "time" => new RelationalScalarType(ScalarKind.Time),
                _ => BuildStringType(schema, sourcePath),
            };
        }

        return BuildStringType(schema, sourcePath);
    }

    private static RelationalScalarType BuildStringType(JsonObject schema, JsonPathExpression sourcePath)
    {
        if (!schema.TryGetPropertyValue("maxLength", out var maxLengthNode) || maxLengthNode is null)
        {
            return new RelationalScalarType(ScalarKind.String);
        }

        if (maxLengthNode is not JsonValue maxLengthValue)
        {
            throw new InvalidOperationException(
                $"Expected maxLength to be a number at {sourcePath.Canonical}."
            );
        }

        var maxLength = maxLengthValue.GetValue<int>();
        if (maxLength <= 0)
        {
            throw new InvalidOperationException(
                $"String schema maxLength must be positive at {sourcePath.Canonical}."
            );
        }

        return new RelationalScalarType(ScalarKind.String, maxLength);
    }

    private static RelationalScalarType ResolveIntegerType(JsonObject schema, JsonPathExpression sourcePath)
    {
        var format = GetOptionalString(schema, "format", sourcePath.Canonical);

        return format switch
        {
            "int64" => new RelationalScalarType(ScalarKind.Int64),
            _ => new RelationalScalarType(ScalarKind.Int32),
        };
    }

    private static RelationalScalarType ResolveDecimalType(
        JsonPathExpression sourcePath,
        RelationalModelBuilderContext context
    )
    {
        if (!context.TryGetDecimalPropertyValidationInfo(sourcePath, out var validationInfo))
        {
            return new RelationalScalarType(ScalarKind.Decimal);
        }

        if (validationInfo.TotalDigits is null || validationInfo.DecimalPlaces is null)
        {
            return new RelationalScalarType(ScalarKind.Decimal);
        }

        if (validationInfo.TotalDigits <= 0 || validationInfo.DecimalPlaces < 0)
        {
            throw new InvalidOperationException(
                $"Decimal property validation info must be positive for {sourcePath.Canonical}."
            );
        }

        if (validationInfo.DecimalPlaces > validationInfo.TotalDigits)
        {
            throw new InvalidOperationException(
                $"Decimal places cannot exceed total digits for {sourcePath.Canonical}."
            );
        }

        return new RelationalScalarType(
            ScalarKind.Decimal,
            Decimal: (validationInfo.TotalDigits.Value, validationInfo.DecimalPlaces.Value)
        );
    }

    private static bool IsXNullable(JsonObject schema, string path)
    {
        if (!schema.TryGetPropertyValue("x-nullable", out var nullableNode) || nullableNode is null)
        {
            return false;
        }

        if (nullableNode is not JsonValue jsonValue)
        {
            throw new InvalidOperationException($"Expected x-nullable to be a boolean at {path}.");
        }

        return jsonValue.GetValue<bool>();
    }

    private static string GetSchemaType(JsonObject schema, string path)
    {
        if (!schema.TryGetPropertyValue("type", out var typeNode) || typeNode is null)
        {
            throw new InvalidOperationException($"Schema type must be specified at {path}.");
        }

        return typeNode switch
        {
            JsonValue jsonValue => jsonValue.GetValue<string>(),
            _ => throw new InvalidOperationException($"Expected type to be a string at {path}.type."),
        };
    }

    private static string? GetOptionalString(JsonObject schema, string propertyName, string path)
    {
        if (!schema.TryGetPropertyValue(propertyName, out var valueNode) || valueNode is null)
        {
            return null;
        }

        return valueNode switch
        {
            JsonValue jsonValue => jsonValue.GetValue<string>(),
            _ => throw new InvalidOperationException(
                $"Expected {propertyName} to be a string at {path}.{propertyName}."
            ),
        };
    }

    private static string BuildColumnBaseName(IReadOnlyList<string> segments)
    {
        if (segments.Count == 0)
        {
            throw new InvalidOperationException("Column path must contain at least one segment.");
        }

        StringBuilder builder = new();

        foreach (var segment in segments)
        {
            builder.Append(RelationalNameConventions.ToPascalCase(segment));
        }

        return builder.ToString();
    }

    private static List<JsonPathSegment> BuildPropertySegments(
        List<JsonPathSegment> pathSegments,
        string propertyName
    )
    {
        List<JsonPathSegment> propertySegments =
        [
            .. pathSegments,
            new JsonPathSegment.Property(propertyName),
        ];

        return propertySegments;
    }

    private static List<string> BuildPropertyColumnSegments(List<string> columnSegments, string propertyName)
    {
        List<string> propertyColumnSegments = [.. columnSegments, propertyName];

        return propertyColumnSegments;
    }

    private static HashSet<string> GetRequiredProperties(
        JsonObject schema,
        List<JsonPathSegment> pathSegments
    )
    {
        var path = JsonPathExpressionCompiler.FromSegments(pathSegments).Canonical;

        if (!schema.TryGetPropertyValue("required", out var requiredNode) || requiredNode is null)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        if (requiredNode is not JsonArray requiredArray)
        {
            throw new InvalidOperationException($"Expected required to be an array at {path}.required.");
        }

        HashSet<string> requiredProperties = new(StringComparer.Ordinal);

        foreach (var requiredEntry in requiredArray)
        {
            if (requiredEntry is null)
            {
                throw new InvalidOperationException(
                    $"Expected required entries to be non-null at {path}.required."
                );
            }

            if (requiredEntry is not JsonValue jsonValue)
            {
                throw new InvalidOperationException(
                    $"Expected required entries to be strings at {path}.required."
                );
            }

            var name = jsonValue.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException(
                    $"Expected required entries to be non-empty at {path}.required."
                );
            }

            requiredProperties.Add(name);
        }

        return requiredProperties;
    }

    private static string BuildPropertyPath(List<JsonPathSegment> scopeSegments, string propertyName)
    {
        List<JsonPathSegment> propertySegments =
        [
            .. scopeSegments,
            new JsonPathSegment.Property(propertyName),
        ];

        return JsonPathExpressionCompiler.FromSegments(propertySegments).Canonical;
    }

    private static void EnsureAllDescriptorPathsUsed(
        RelationalModelBuilderContext context,
        HashSet<string> usedDescriptorPaths
    )
    {
        if (context.DescriptorPathsByJsonPath.Count == 0)
        {
            return;
        }

        if (usedDescriptorPaths.Count == context.DescriptorPathsByJsonPath.Count)
        {
            return;
        }

        var missingPaths = context
            .DescriptorPathsByJsonPath.Keys.Where(path => !usedDescriptorPaths.Contains(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        if (missingPaths.Length == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Descriptor paths were not found in JSON schema: {string.Join(", ", missingPaths)}."
        );
    }

    private static string BuildForeignKeyName(string tableName, IReadOnlyList<DbColumnName> columns)
    {
        if (columns.Count == 0)
        {
            throw new InvalidOperationException("Foreign key must have at least one column.");
        }

        var columnSuffix = string.Join("_", columns.Select(column => column.Value));

        return $"FK_{tableName}_{columnSuffix}";
    }

    private static SchemaKind DetermineSchemaKind(JsonObject schema)
    {
        if (schema.TryGetPropertyValue("type", out var typeNode) && typeNode is JsonValue jsonValue)
        {
            var schemaType = jsonValue.GetValue<string>();

            return schemaType switch
            {
                "object" => SchemaKind.Object,
                "array" => SchemaKind.Array,
                _ => SchemaKind.Scalar,
            };
        }

        if (schema.ContainsKey("items"))
        {
            return SchemaKind.Array;
        }

        if (schema.ContainsKey("properties"))
        {
            return SchemaKind.Object;
        }

        return SchemaKind.Scalar;
    }

    private sealed class TableBuilder
    {
        private readonly HashSet<string> _columnNames = new(StringComparer.Ordinal);

        public TableBuilder(DbTableModel table)
        {
            Definition = table;
            Columns = new List<DbColumnModel>(table.Columns);
            Constraints = new List<TableConstraint>(table.Constraints);

            foreach (var keyColumn in table.Key.Columns)
            {
                _columnNames.Add(keyColumn.ColumnName.Value);
            }

            foreach (var column in table.Columns)
            {
                _columnNames.Add(column.ColumnName.Value);
            }
        }

        public DbTableModel Definition { get; }

        public List<DbColumnModel> Columns { get; }

        public List<TableConstraint> Constraints { get; }

        public void AddColumn(DbColumnModel column)
        {
            if (!_columnNames.Add(column.ColumnName.Value))
            {
                var tableName = Definition.Table.Name;

                throw new InvalidOperationException(
                    $"Column name '{column.ColumnName.Value}' is already defined on table '{tableName}'."
                );
            }

            Columns.Add(column);
        }

        public void AddConstraint(TableConstraint constraint)
        {
            Constraints.Add(constraint);
        }

        public DbTableModel Build()
        {
            return Definition with { Columns = Columns.ToArray(), Constraints = Constraints.ToArray() };
        }
    }

    private enum SchemaKind
    {
        Object,
        Array,
        Scalar,
    }
}
