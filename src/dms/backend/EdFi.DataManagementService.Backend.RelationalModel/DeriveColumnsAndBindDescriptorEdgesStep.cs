// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Backend.RelationalModel;

/// <summary>
/// Derives scalar columns (and descriptor foreign keys) for each previously-discovered table scope.
/// <para>
/// This step walks <c>resourceSchema.jsonSchemaForInsert</c> and:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// Inlines object properties (except <c>_ext</c>) by prefixing descendant scalar property names.
/// </description>
/// </item>
/// <item>
/// <description>
/// Routes array item properties to the child table at that array's JSONPath scope.
/// </description>
/// </item>
/// <item>
/// <description>
/// Converts descriptor value paths provided by <see cref="RelationalModelBuilderContext.DescriptorPathsByJsonPath"/>
/// into <c>*_DescriptorId</c> FK columns and records <see cref="DescriptorEdgeSource"/> metadata.
/// </description>
/// </item>
/// </list>
/// <para>
/// Nullability is derived from JSON Schema required-ness, <c>x-nullable</c>, and whether the value has an
/// optional ancestor object.
/// </para>
/// </summary>
public sealed class DeriveColumnsAndBindDescriptorEdgesStep : IRelationalModelBuilderStep
{
    private const string ExtensionPropertyName = "_ext";
    private static readonly DbSchemaName _dmsSchemaName = new("dms");
    private static readonly DbTableName _descriptorTableName = new(_dmsSchemaName, "Descriptor");

    /// <summary>
    /// Populates derived scalar/descriptor columns for each table in the current <see cref="RelationalResourceModel"/>.
    /// </summary>
    /// <param name="context">The builder context containing schema inputs and the partially-derived model.</param>
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

        JsonSchemaUnsupportedKeywordValidator.Validate(rootSchema, "$");

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
        var referenceIdentityPaths = BuildReferenceIdentityPathSet(context.DocumentReferenceMappings);
        var referenceObjectPaths = BuildReferenceObjectPathSet(context.DocumentReferenceMappings);
        HashSet<string> usedDescriptorPaths = new(StringComparer.Ordinal);
        List<DescriptorEdgeSource> descriptorEdgeSources = [];

        WalkSchema(
            rootSchema,
            rootTable,
            [],
            [],
            "$",
            hasOptionalAncestor: false,
            context,
            tableBuilders,
            identityPaths,
            referenceIdentityPaths,
            referenceObjectPaths,
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
            DescriptorEdgeSources = descriptorEdgeSources.ToArray(),
        };
    }

    /// <summary>
    /// Walks a schema node and dispatches to the appropriate object/array visitor.
    /// </summary>
    private static void WalkSchema(
        JsonObject schema,
        TableBuilder tableBuilder,
        List<JsonPathSegment> pathSegments,
        List<string> columnSegments,
        string schemaPath,
        bool hasOptionalAncestor,
        RelationalModelBuilderContext context,
        IReadOnlyDictionary<string, TableBuilder> tableBuilders,
        HashSet<string> identityPaths,
        IReadOnlySet<string> referenceIdentityPaths,
        IReadOnlySet<string> referenceObjectPaths,
        HashSet<string> usedDescriptorPaths,
        List<DescriptorEdgeSource> descriptorEdgeSources
    )
    {
        var currentPath = JsonPathExpressionCompiler.FromSegments(pathSegments).Canonical;
        var schemaKind = JsonSchemaTraversalConventions.DetermineSchemaKind(schema);

        switch (schemaKind)
        {
            case SchemaKind.Object:
                WalkObjectSchema(
                    schema,
                    tableBuilder,
                    pathSegments,
                    columnSegments,
                    schemaPath,
                    hasOptionalAncestor,
                    context,
                    tableBuilders,
                    identityPaths,
                    referenceIdentityPaths,
                    referenceObjectPaths,
                    usedDescriptorPaths,
                    descriptorEdgeSources
                );
                break;
            case SchemaKind.Array:
                WalkArraySchema(
                    schema,
                    pathSegments,
                    schemaPath,
                    context,
                    tableBuilders,
                    identityPaths,
                    referenceIdentityPaths,
                    referenceObjectPaths,
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

    /// <summary>
    /// Walks an object schema, descending into nested objects/arrays and producing scalar/descriptor columns
    /// for scalar properties.
    /// </summary>
    private static void WalkObjectSchema(
        JsonObject schema,
        TableBuilder tableBuilder,
        List<JsonPathSegment> pathSegments,
        List<string> columnSegments,
        string schemaPath,
        bool hasOptionalAncestor,
        RelationalModelBuilderContext context,
        IReadOnlyDictionary<string, TableBuilder> tableBuilders,
        HashSet<string> identityPaths,
        IReadOnlySet<string> referenceIdentityPaths,
        IReadOnlySet<string> referenceObjectPaths,
        HashSet<string> usedDescriptorPaths,
        List<DescriptorEdgeSource> descriptorEdgeSources
    )
    {
        if (!schema.TryGetPropertyValue("properties", out var propertiesNode) || propertiesNode is null)
        {
            return;
        }

        var scopePath = JsonPathExpressionCompiler.FromSegments(pathSegments).Canonical;

        if (propertiesNode is not JsonObject propertiesObject)
        {
            throw new InvalidOperationException($"Expected properties to be an object at {scopePath}.");
        }

        var requiredProperties = GetRequiredProperties(schema, pathSegments);
        var isReferenceScope = referenceObjectPaths.Contains(scopePath);

        foreach (var property in propertiesObject.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (string.Equals(property.Key, ExtensionPropertyName, StringComparison.Ordinal))
            {
                continue;
            }

            if (isReferenceScope && string.Equals(property.Key, "link", StringComparison.Ordinal))
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
            var propertySchemaPath = $"{schemaPath}.properties.{property.Key}";
            var isRequired = requiredProperties.Contains(property.Key);
            var isXNullable = IsXNullable(propertySchema, propertyPath.Canonical);
            var isOptional = !isRequired;
            var isNullable = hasOptionalAncestor || isOptional || isXNullable;
            var nextHasOptionalAncestor = hasOptionalAncestor || isOptional || isXNullable;

            JsonSchemaUnsupportedKeywordValidator.Validate(propertySchema, propertySchemaPath);

            var schemaKind = JsonSchemaTraversalConventions.DetermineSchemaKind(propertySchema);
            switch (schemaKind)
            {
                case SchemaKind.Object:
                case SchemaKind.Array:
                    WalkSchema(
                        propertySchema,
                        tableBuilder,
                        propertyPathSegments,
                        propertyColumnSegments,
                        propertySchemaPath,
                        nextHasOptionalAncestor,
                        context,
                        tableBuilders,
                        identityPaths,
                        referenceIdentityPaths,
                        referenceObjectPaths,
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
                        referenceIdentityPaths,
                        usedDescriptorPaths,
                        descriptorEdgeSources
                    );
                    break;
                default:
                    throw new InvalidOperationException($"Unknown schema kind at {propertyPath.Canonical}.");
            }
        }
    }

    /// <summary>
    /// Walks an array schema by switching traversal to the derived child-table scope for that array
    /// (<c>$.path.to.array[*]</c>).
    /// </summary>
    private static void WalkArraySchema(
        JsonObject schema,
        List<JsonPathSegment> propertySegments,
        string schemaPath,
        RelationalModelBuilderContext context,
        IReadOnlyDictionary<string, TableBuilder> tableBuilders,
        HashSet<string> identityPaths,
        IReadOnlySet<string> referenceIdentityPaths,
        IReadOnlySet<string> referenceObjectPaths,
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

        var itemsSchemaPath = $"{schemaPath}.items";

        JsonSchemaUnsupportedKeywordValidator.Validate(itemsSchema, itemsSchemaPath);

        List<JsonPathSegment> arraySegments = [.. propertySegments, new JsonPathSegment.AnyArrayElement()];

        var arrayScope = JsonPathExpressionCompiler.FromSegments(arraySegments).Canonical;

        if (!tableBuilders.TryGetValue(arrayScope, out var childTable))
        {
            throw new InvalidOperationException($"Child table scope '{arrayScope}' was not found.");
        }

        var itemsKind = JsonSchemaTraversalConventions.DetermineSchemaKind(itemsSchema);

        switch (itemsKind)
        {
            case SchemaKind.Object:
                WalkSchema(
                    itemsSchema,
                    childTable,
                    arraySegments,
                    [],
                    itemsSchemaPath,
                    hasOptionalAncestor: false,
                    context,
                    tableBuilders,
                    identityPaths,
                    referenceIdentityPaths,
                    referenceObjectPaths,
                    usedDescriptorPaths,
                    descriptorEdgeSources
                );
                break;
            case SchemaKind.Scalar:
                AddDescriptorArrayColumn(
                    childTable,
                    itemsSchema,
                    propertySegments,
                    arraySegments,
                    context,
                    identityPaths,
                    referenceIdentityPaths,
                    usedDescriptorPaths,
                    descriptorEdgeSources
                );
                break;
            case SchemaKind.Array:
                throw new InvalidOperationException($"Array schema items must be an object at {arrayPath}.");
            default:
                throw new InvalidOperationException($"Unknown schema kind at {arrayPath}.");
        }
    }

    /// <summary>
    /// Handles arrays of descriptor strings by treating each element as a descriptor value path and emitting
    /// a descriptor FK column on the array's child table.
    /// </summary>
    private static void AddDescriptorArrayColumn(
        TableBuilder tableBuilder,
        JsonObject itemsSchema,
        List<JsonPathSegment> propertySegments,
        List<JsonPathSegment> arraySegments,
        RelationalModelBuilderContext context,
        HashSet<string> identityPaths,
        IReadOnlySet<string> referenceIdentityPaths,
        HashSet<string> usedDescriptorPaths,
        List<DescriptorEdgeSource> descriptorEdgeSources
    )
    {
        var elementPath = JsonPathExpressionCompiler.FromSegments(arraySegments);

        if (!context.TryGetDescriptorPath(elementPath, out _))
        {
            var arrayPath = JsonPathExpressionCompiler.FromSegments(propertySegments).Canonical;

            throw new InvalidOperationException($"Array schema items must be an object at {arrayPath}.");
        }

        var columnSegments = BuildDescriptorArrayColumnSegments(propertySegments);
        var isNullable = IsXNullable(itemsSchema, elementPath.Canonical);

        AddScalarOrDescriptorColumn(
            tableBuilder,
            itemsSchema,
            columnSegments,
            elementPath,
            isNullable,
            context,
            identityPaths,
            referenceIdentityPaths,
            usedDescriptorPaths,
            descriptorEdgeSources
        );
    }

    /// <summary>
    /// Adds either a scalar column or (when the JSONPath is a descriptor path) a descriptor FK column.
    /// </summary>
    private static void AddScalarOrDescriptorColumn(
        TableBuilder tableBuilder,
        JsonObject schema,
        IReadOnlyList<string> columnSegments,
        JsonPathExpression sourcePath,
        bool isNullable,
        RelationalModelBuilderContext context,
        HashSet<string> identityPaths,
        IReadOnlySet<string> referenceIdentityPaths,
        HashSet<string> usedDescriptorPaths,
        List<DescriptorEdgeSource> descriptorEdgeSources
    )
    {
        if (referenceIdentityPaths.Contains(sourcePath.Canonical))
        {
            if (context.TryGetDescriptorPath(sourcePath, out _))
            {
                usedDescriptorPaths.Add(sourcePath.Canonical);
            }

            return;
        }

        if (identityPaths.Contains(sourcePath.Canonical) && isNullable)
        {
            var projectName = context.ProjectName ?? "Unknown";
            var resourceName = context.ResourceName ?? "Unknown";

            throw new InvalidOperationException(
                $"Identity path '{sourcePath.Canonical}' on resource '{projectName}:{resourceName}' "
                    + "maps to a nullable column. Identity components must be non-null."
            );
        }

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
                    RelationalNameConventions.ForeignKeyName(
                        tableBuilder.Definition.Table.Name,
                        new[] { columnName }
                    ),
                    new[] { columnName },
                    _descriptorTableName,
                    new[] { RelationalNameConventions.DocumentIdColumnName },
                    OnDelete: ReferentialAction.NoAction,
                    OnUpdate: ReferentialAction.NoAction
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

        var scalarType = RelationalScalarTypeResolver.ResolveScalarType(schema, sourcePath, context);
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

    private static HashSet<string> BuildReferenceIdentityPathSet(
        IReadOnlyList<DocumentReferenceMapping> mappings
    )
    {
        HashSet<string> referenceIdentityPaths = new(StringComparer.Ordinal);

        if (mappings.Count == 0)
        {
            return referenceIdentityPaths;
        }

        foreach (var mapping in mappings)
        {
            foreach (var binding in mapping.ReferenceJsonPaths)
            {
                referenceIdentityPaths.Add(binding.ReferenceJsonPath.Canonical);
            }
        }

        return referenceIdentityPaths;
    }

    private static HashSet<string> BuildReferenceObjectPathSet(
        IReadOnlyList<DocumentReferenceMapping> mappings
    )
    {
        HashSet<string> referenceObjectPaths = new(StringComparer.Ordinal);

        if (mappings.Count == 0)
        {
            return referenceObjectPaths;
        }

        foreach (var mapping in mappings)
        {
            referenceObjectPaths.Add(mapping.ReferenceObjectPath.Canonical);
        }

        return referenceObjectPaths;
    }

    /// <summary>
    /// Reads <c>x-nullable</c> (an OpenAPI extension commonly used in Ed-Fi schemas) as an override for
    /// JSON Schema required-ness.
    /// </summary>
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

    /// <summary>
    /// Builds the physical column base name from the accumulated column-segment path using PascalCase.
    /// </summary>
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

    /// <summary>
    /// For arrays of descriptor strings, derives the base column name from the owning array property.
    /// </summary>
    private static List<string> BuildDescriptorArrayColumnSegments(
        IReadOnlyList<JsonPathSegment> propertySegments
    )
    {
        if (propertySegments.Count == 0 || propertySegments[^1] is not JsonPathSegment.Property property)
        {
            throw new InvalidOperationException("Array schema must be rooted at a property segment.");
        }

        var singular = RelationalNameConventions.SingularizeCollectionSegment(property.Name);
        List<string> columnSegments = [singular];

        return columnSegments;
    }

    /// <summary>
    /// Appends a property segment to an existing JSONPath segment list.
    /// </summary>
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

    /// <summary>
    /// Appends a property name to the current column-prefix segment list.
    /// </summary>
    private static List<string> BuildPropertyColumnSegments(List<string> columnSegments, string propertyName)
    {
        List<string> propertyColumnSegments = [.. columnSegments, propertyName];

        return propertyColumnSegments;
    }

    /// <summary>
    /// Reads and validates the <c>required</c> array on an object schema.
    /// </summary>
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

    /// <summary>
    /// Builds the canonical JSONPath for a single property under the provided scope segments.
    /// </summary>
    private static string BuildPropertyPath(List<JsonPathSegment> scopeSegments, string propertyName)
    {
        List<JsonPathSegment> propertySegments =
        [
            .. scopeSegments,
            new JsonPathSegment.Property(propertyName),
        ];

        return JsonPathExpressionCompiler.FromSegments(propertySegments).Canonical;
    }

    /// <summary>
    /// Ensures the descriptor metadata provided to the builder has a corresponding value in the JSON schema.
    /// </summary>
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

    /// <summary>
    /// Mutable builder for a <see cref="DbTableModel"/> that enforces unique column names and accumulates
    /// constraints during schema traversal.
    /// </summary>
    private sealed class TableBuilder
    {
        private readonly Dictionary<string, JsonPathExpression?> _columnSources = new(StringComparer.Ordinal);

        /// <summary>
        /// Initializes a builder using an existing table definition (typically containing only key columns).
        /// </summary>
        public TableBuilder(DbTableModel table)
        {
            Definition = table;
            Columns = new List<DbColumnModel>(table.Columns);
            Constraints = new List<TableConstraint>(table.Constraints);

            foreach (var column in table.Columns)
            {
                _columnSources[column.ColumnName.Value] = column.SourceJsonPath;
            }

            foreach (var keyColumn in table.Key.Columns)
            {
                _columnSources.TryAdd(keyColumn.ColumnName.Value, null);
            }
        }

        /// <summary>
        /// Gets the immutable table definition being built (schema/name, key columns, and JSON scope).
        /// </summary>
        public DbTableModel Definition { get; }

        /// <summary>
        /// Gets the mutable column inventory for the table.
        /// </summary>
        public List<DbColumnModel> Columns { get; }

        /// <summary>
        /// Gets the mutable constraint inventory for the table.
        /// </summary>
        public List<TableConstraint> Constraints { get; }

        /// <summary>
        /// Adds a column, failing fast when a physical column name collides across different JSON source
        /// paths.
        /// </summary>
        public void AddColumn(DbColumnModel column)
        {
            if (_columnSources.TryGetValue(column.ColumnName.Value, out var existingSource))
            {
                var tableName = Definition.Table.Name;
                var existingPath = ResolveSourcePath(existingSource);
                var incomingPath = ResolveSourcePath(column.SourceJsonPath);

                throw new InvalidOperationException(
                    $"Column name '{column.ColumnName.Value}' is already defined on table '{tableName}'. "
                        + $"Colliding source paths '{existingPath}' and '{incomingPath}'. "
                        + "Use relational.nameOverrides to resolve the collision."
                );
            }

            _columnSources.Add(column.ColumnName.Value, column.SourceJsonPath);
            Columns.Add(column);
        }

        /// <summary>
        /// Adds a table constraint (FKs, unique constraints, etc.) to the builder.
        /// </summary>
        public void AddConstraint(TableConstraint constraint)
        {
            Constraints.Add(constraint);
        }

        /// <summary>
        /// Returns a new immutable <see cref="DbTableModel"/> with the accumulated column and constraint
        /// inventory.
        /// </summary>
        public DbTableModel Build()
        {
            return Definition with { Columns = Columns.ToArray(), Constraints = Constraints.ToArray() };
        }

        /// <summary>
        /// Converts a column source path into the most helpful canonical JSONPath for collision messages.
        /// </summary>
        private string ResolveSourcePath(JsonPathExpression? sourcePath)
        {
            return (sourcePath ?? Definition.JsonScope).Canonical;
        }
    }
}
