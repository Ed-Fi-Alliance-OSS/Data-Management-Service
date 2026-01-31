// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using static EdFi.DataManagementService.Backend.RelationalModel.RelationalModelSetSchemaHelpers;

namespace EdFi.DataManagementService.Backend.RelationalModel;

/// <summary>
/// Derives root-level uniqueness constraints and descriptor-system columns from identity metadata.
/// </summary>
public sealed class ConstraintDerivationRelationalModelSetPass : IRelationalModelSetPass
{
    private const string UriColumnLabel = "Uri";
    private const string DiscriminatorColumnLabel = "Discriminator";
    private const int UriMaxLength = 306;
    private const int DiscriminatorMaxLength = 128;

    /// <summary>
    /// The explicit order for the constraint derivation pass.
    /// </summary>
    public int Order { get; } = 40;

    /// <summary>
    /// Derives root-table unique constraints for each concrete resource.
    /// </summary>
    /// <param name="context">The shared set-level builder context.</param>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var resourcesByKey = context
            .ConcreteResourcesInNameOrder.Select((model, index) => new ResourceEntry(index, model))
            .ToDictionary(entry => entry.Model.ResourceKey.Resource, entry => entry);

        Dictionary<string, JsonObject> apiSchemaRootsByProjectEndpoint = new(StringComparer.Ordinal);

        foreach (var resourceContext in context.EnumerateConcreteResourceSchemasInNameOrder())
        {
            if (IsResourceExtension(resourceContext))
            {
                continue;
            }

            var resource = new QualifiedResourceName(
                resourceContext.Project.ProjectSchema.ProjectName,
                resourceContext.ResourceName
            );

            if (!resourcesByKey.TryGetValue(resource, out var entry))
            {
                throw new InvalidOperationException(
                    $"Concrete resource '{FormatResource(resource)}' was not found for constraint derivation."
                );
            }

            var builderContext = BuildResourceContext(resourceContext, apiSchemaRootsByProjectEndpoint);
            var updatedModel = ApplyRootConstraints(builderContext, entry.Model.RelationalModel, resource);

            if (ReferenceEquals(updatedModel, entry.Model.RelationalModel))
            {
                continue;
            }

            context.ConcreteResourcesInNameOrder[entry.Index] = entry.Model with
            {
                RelationalModel = updatedModel,
            };
        }
    }

    private static RelationalResourceModel ApplyRootConstraints(
        RelationalModelBuilderContext builderContext,
        RelationalResourceModel resourceModel,
        QualifiedResourceName resource
    )
    {
        var rootTable = resourceModel.Root;
        var tableBuilder = new TableBuilder(rootTable);
        var mutated = false;

        if (resourceModel.StorageKind == ResourceStorageKind.SharedDescriptorTable)
        {
            mutated |= EnsureDescriptorColumn(tableBuilder, rootTable, BuildUriColumn(), UriColumnLabel);
            mutated |= EnsureDescriptorColumn(
                tableBuilder,
                rootTable,
                BuildDiscriminatorColumn(),
                DiscriminatorColumnLabel
            );

            var descriptorUniqueColumns = new DbColumnName[]
            {
                new(UriColumnLabel),
                new(DiscriminatorColumnLabel),
            };
            var descriptorUniqueName = BuildUniqueConstraintName(
                rootTable.Table.Name,
                descriptorUniqueColumns
            );

            if (!ContainsUniqueConstraint(rootTable.Constraints, descriptorUniqueName))
            {
                tableBuilder.AddConstraint(
                    new TableConstraint.Unique(descriptorUniqueName, descriptorUniqueColumns)
                );
                mutated = true;
            }

            if (!mutated)
            {
                return resourceModel;
            }

            var updatedRoot = CanonicalizeTable(tableBuilder.Build());

            return UpdateResourceModel(resourceModel, updatedRoot);
        }

        var identityColumns = BuildRootIdentityColumns(resourceModel, builderContext, resource);

        if (identityColumns.Count == 0)
        {
            return resourceModel;
        }

        var rootUniqueName = BuildUniqueConstraintName(rootTable.Table.Name, identityColumns);

        if (!ContainsUniqueConstraint(rootTable.Constraints, rootUniqueName))
        {
            tableBuilder.AddConstraint(new TableConstraint.Unique(rootUniqueName, identityColumns));
            mutated = true;
        }

        if (!mutated)
        {
            return resourceModel;
        }

        var updatedRootTable = CanonicalizeTable(tableBuilder.Build());

        return UpdateResourceModel(resourceModel, updatedRootTable);
    }

    private static RelationalResourceModel UpdateResourceModel(
        RelationalResourceModel resourceModel,
        DbTableModel updatedRoot
    )
    {
        var updatedTables = resourceModel
            .TablesInReadDependencyOrder.Select(table =>
                table.JsonScope.Canonical == updatedRoot.JsonScope.Canonical ? updatedRoot : table
            )
            .ToArray();

        return resourceModel with
        {
            Root = updatedRoot,
            TablesInReadDependencyOrder = updatedTables,
            TablesInWriteDependencyOrder = updatedTables,
        };
    }

    private static IReadOnlyList<DbColumnName> BuildRootIdentityColumns(
        RelationalResourceModel resourceModel,
        RelationalModelBuilderContext builderContext,
        QualifiedResourceName resource
    )
    {
        if (builderContext.IdentityJsonPaths.Count == 0)
        {
            return Array.Empty<DbColumnName>();
        }

        var rootTable = resourceModel.Root;
        var rootColumnsByPath = rootTable
            .Columns.Where(column => column.SourceJsonPath is not null)
            .GroupBy(column => column.SourceJsonPath!.Value.Canonical, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().ColumnName, StringComparer.Ordinal);
        var referenceBindingsByIdentityPath = BuildReferenceIdentityBindings(
            resourceModel.DocumentReferenceBindings,
            resource
        );

        HashSet<string> seenColumns = new(StringComparer.Ordinal);
        List<DbColumnName> uniqueColumns = new(builderContext.IdentityJsonPaths.Count);

        foreach (var identityPath in builderContext.IdentityJsonPaths)
        {
            if (identityPath.Segments.Any(segment => segment is JsonPathSegment.AnyArrayElement))
            {
                throw new InvalidOperationException(
                    $"Identity path '{identityPath.Canonical}' on resource '{FormatResource(resource)}' "
                        + "must not include array segments when deriving root unique constraints."
                );
            }

            if (referenceBindingsByIdentityPath.TryGetValue(identityPath.Canonical, out var binding))
            {
                if (binding.Table != rootTable.Table)
                {
                    throw new InvalidOperationException(
                        $"Identity path '{identityPath.Canonical}' on resource '{FormatResource(resource)}' "
                            + "must bind to the root table when deriving unique constraints."
                    );
                }

                AddUniqueColumn(binding.FkColumn, uniqueColumns, seenColumns);
                continue;
            }

            if (!rootColumnsByPath.TryGetValue(identityPath.Canonical, out var columnName))
            {
                throw new InvalidOperationException(
                    $"Identity path '{identityPath.Canonical}' on resource '{FormatResource(resource)}' "
                        + "did not map to a root table column."
                );
            }

            AddUniqueColumn(columnName, uniqueColumns, seenColumns);
        }

        return uniqueColumns.ToArray();
    }

    private static IReadOnlyDictionary<string, DocumentReferenceBinding> BuildReferenceIdentityBindings(
        IReadOnlyList<DocumentReferenceBinding> bindings,
        QualifiedResourceName resource
    )
    {
        Dictionary<string, DocumentReferenceBinding> lookup = new(StringComparer.Ordinal);

        foreach (var binding in bindings)
        {
            foreach (var identityBinding in binding.IdentityBindings)
            {
                if (!lookup.TryAdd(identityBinding.ReferenceJsonPath.Canonical, binding))
                {
                    var existing = lookup[identityBinding.ReferenceJsonPath.Canonical];

                    if (existing.ReferenceObjectPath.Canonical == binding.ReferenceObjectPath.Canonical)
                    {
                        continue;
                    }

                    throw new InvalidOperationException(
                        $"Identity path '{identityBinding.ReferenceJsonPath.Canonical}' on resource "
                            + $"'{FormatResource(resource)}' was bound to multiple references."
                    );
                }
            }
        }

        return lookup;
    }

    private static void AddUniqueColumn(
        DbColumnName columnName,
        ICollection<DbColumnName> columns,
        ISet<string> seenColumns
    )
    {
        if (!seenColumns.Add(columnName.Value))
        {
            return;
        }

        columns.Add(columnName);
    }

    private static bool EnsureDescriptorColumn(
        TableBuilder tableBuilder,
        DbTableModel rootTable,
        DbColumnModel column,
        string columnName
    )
    {
        if (
            rootTable.Columns.Any(existing =>
                string.Equals(existing.ColumnName.Value, columnName, StringComparison.Ordinal)
            )
        )
        {
            return false;
        }

        tableBuilder.AddColumn(column);

        return true;
    }

    private static DbColumnModel BuildUriColumn()
    {
        return new DbColumnModel(
            new DbColumnName(UriColumnLabel),
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.String, UriMaxLength),
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
    }

    private static DbColumnModel BuildDiscriminatorColumn()
    {
        return new DbColumnModel(
            new DbColumnName(DiscriminatorColumnLabel),
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.String, DiscriminatorMaxLength),
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
    }

    private static string BuildUniqueConstraintName(string tableName, IReadOnlyList<DbColumnName> columns)
    {
        if (columns.Count == 0)
        {
            throw new InvalidOperationException("Unique constraint must include at least one column.");
        }

        return $"UX_{tableName}_{string.Join("_", columns.Select(column => column.Value))}";
    }

    private static bool ContainsUniqueConstraint(IReadOnlyList<TableConstraint> constraints, string name)
    {
        return constraints
            .OfType<TableConstraint.Unique>()
            .Any(constraint => string.Equals(constraint.Name, name, StringComparison.Ordinal));
    }

    private static DbTableModel CanonicalizeTable(DbTableModel table)
    {
        var keyColumnOrder = BuildKeyColumnOrder(table.Key.Columns);

        var orderedColumns = table
            .Columns.OrderBy(column => GetColumnGroup(column, keyColumnOrder))
            .ThenBy(column => GetColumnKeyIndex(column, keyColumnOrder))
            .ThenBy(column => column.ColumnName.Value, StringComparer.Ordinal)
            .ToArray();

        var orderedConstraints = table
            .Constraints.OrderBy(GetConstraintGroup)
            .ThenBy(GetConstraintName, StringComparer.Ordinal)
            .ToArray();

        return table with
        {
            Columns = orderedColumns,
            Constraints = orderedConstraints,
        };
    }

    private static Dictionary<string, int> BuildKeyColumnOrder(IReadOnlyList<DbKeyColumn> keyColumns)
    {
        Dictionary<string, int> keyOrder = new(StringComparer.Ordinal);

        for (var index = 0; index < keyColumns.Count; index++)
        {
            keyOrder[keyColumns[index].ColumnName.Value] = index;
        }

        return keyOrder;
    }

    private static int GetColumnGroup(DbColumnModel column, IReadOnlyDictionary<string, int> keyColumnOrder)
    {
        if (keyColumnOrder.ContainsKey(column.ColumnName.Value))
        {
            return 0;
        }

        return column.Kind switch
        {
            ColumnKind.DescriptorFk => 1,
            ColumnKind.Scalar => 2,
            _ => 3,
        };
    }

    private static int GetColumnKeyIndex(
        DbColumnModel column,
        IReadOnlyDictionary<string, int> keyColumnOrder
    )
    {
        return keyColumnOrder.TryGetValue(column.ColumnName.Value, out var index) ? index : int.MaxValue;
    }

    private static int GetConstraintGroup(TableConstraint constraint)
    {
        return constraint switch
        {
            TableConstraint.Unique => 1,
            TableConstraint.ForeignKey => 2,
            TableConstraint.AllOrNoneNullability => 3,
            _ => 99,
        };
    }

    private static string GetConstraintName(TableConstraint constraint)
    {
        return constraint switch
        {
            TableConstraint.Unique unique => unique.Name,
            TableConstraint.ForeignKey foreignKey => foreignKey.Name,
            TableConstraint.AllOrNoneNullability allOrNone => allOrNone.Name,
            _ => string.Empty,
        };
    }

    private static RelationalModelBuilderContext BuildResourceContext(
        ConcreteResourceSchemaContext resourceContext,
        IDictionary<string, JsonObject> apiSchemaRootsByProjectEndpoint
    )
    {
        var projectSchema = resourceContext.Project.ProjectSchema;
        var apiSchemaRoot = GetApiSchemaRoot(
            apiSchemaRootsByProjectEndpoint,
            projectSchema.ProjectEndpointName,
            resourceContext.Project.EffectiveProject.ProjectSchema
        );

        var builderContext = new RelationalModelBuilderContext
        {
            ApiSchemaRoot = apiSchemaRoot,
            ResourceEndpointName = resourceContext.ResourceEndpointName,
        };

        new ExtractInputsStep().Execute(builderContext);

        return builderContext;
    }

    private static bool IsResourceExtension(ConcreteResourceSchemaContext resourceContext)
    {
        if (
            !resourceContext.ResourceSchema.TryGetPropertyValue(
                "isResourceExtension",
                out var resourceExtensionNode
            ) || resourceExtensionNode is null
        )
        {
            throw new InvalidOperationException(
                $"Expected isResourceExtension to be on ResourceSchema for resource "
                    + $"'{resourceContext.Project.ProjectSchema.ProjectName}:{resourceContext.ResourceName}', "
                    + "invalid ApiSchema."
            );
        }

        return resourceExtensionNode switch
        {
            JsonValue jsonValue => jsonValue.GetValue<bool>(),
            _ => throw new InvalidOperationException(
                $"Expected isResourceExtension to be a boolean for resource "
                    + $"'{resourceContext.Project.ProjectSchema.ProjectName}:{resourceContext.ResourceName}', "
                    + "invalid ApiSchema."
            ),
        };
    }

    private static JsonObject GetApiSchemaRoot(
        IDictionary<string, JsonObject> apiSchemaRootsByProjectEndpoint,
        string projectEndpointName,
        JsonObject projectSchema
    )
    {
        if (apiSchemaRootsByProjectEndpoint.TryGetValue(projectEndpointName, out var apiSchemaRoot))
        {
            return apiSchemaRoot;
        }

        var detachedSchema = projectSchema.DeepClone();

        if (detachedSchema is not JsonObject detachedObject)
        {
            throw new InvalidOperationException("Project schema must be an object.");
        }

        apiSchemaRoot = new JsonObject { ["projectSchema"] = detachedObject };

        apiSchemaRootsByProjectEndpoint[projectEndpointName] = apiSchemaRoot;

        return apiSchemaRoot;
    }

    private sealed record ResourceEntry(int Index, ConcreteResourceModel Model);

    private sealed class TableBuilder
    {
        private readonly Dictionary<string, JsonPathExpression?> _columnSources = new(StringComparer.Ordinal);

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

        public DbTableModel Definition { get; }

        public List<DbColumnModel> Columns { get; }

        public List<TableConstraint> Constraints { get; }

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

        public void AddConstraint(TableConstraint constraint)
        {
            Constraints.Add(constraint);
        }

        public DbTableModel Build()
        {
            return Definition with { Columns = Columns.ToArray(), Constraints = Constraints.ToArray() };
        }

        private string ResolveSourcePath(JsonPathExpression? sourcePath)
        {
            return (sourcePath ?? Definition.JsonScope).Canonical;
        }
    }
}
