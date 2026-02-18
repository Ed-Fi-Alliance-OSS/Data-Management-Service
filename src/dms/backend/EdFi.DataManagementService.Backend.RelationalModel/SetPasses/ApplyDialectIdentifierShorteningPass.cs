// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using static EdFi.DataManagementService.Backend.RelationalModel.Schema.RelationalModelSetSchemaHelpers;

namespace EdFi.DataManagementService.Backend.RelationalModel.SetPasses;

/// <summary>
/// Applies dialect-specific identifier shortening across the derived relational model set.
/// </summary>
public sealed class ApplyDialectIdentifierShorteningPass : IRelationalModelSetPass
{
    /// <summary>
    /// Executes the pass.
    /// </summary>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        ValidateIdentifierShorteningCollisions(context);
        ApplyDialectShortening(context, context.DialectRules);
    }

    /// <summary>
    /// Applies dialect-specific shortening to all identifiers tracked in the builder context.
    /// </summary>
    private static void ApplyDialectShortening(
        RelationalModelSetBuilderContext context,
        ISqlDialectRules dialectRules
    )
    {
        UpdateProjectSchemas(context, dialectRules);

        for (var index = 0; index < context.ConcreteResourcesInNameOrder.Count; index++)
        {
            var entry = context.ConcreteResourcesInNameOrder[index];
            var updatedModel = ApplyToResource(entry.RelationalModel, dialectRules, out var changed);

            if (!changed)
            {
                continue;
            }

            context.ConcreteResourcesInNameOrder[index] = entry with { RelationalModel = updatedModel };
        }

        for (var index = 0; index < context.AbstractIdentityTablesInNameOrder.Count; index++)
        {
            var entry = context.AbstractIdentityTablesInNameOrder[index];
            var updatedTable = ApplyToTable(entry.TableModel, dialectRules, out var changed);

            if (!changed)
            {
                continue;
            }

            context.AbstractIdentityTablesInNameOrder[index] = entry with { TableModel = updatedTable };
        }

        for (var index = 0; index < context.AbstractUnionViewsInNameOrder.Count; index++)
        {
            var entry = context.AbstractUnionViewsInNameOrder[index];
            var updatedView = ApplyToUnionView(entry, dialectRules, out var changed);

            if (!changed)
            {
                continue;
            }

            context.AbstractUnionViewsInNameOrder[index] = updatedView;
        }

        for (var index = 0; index < context.IndexInventory.Count; index++)
        {
            var entry = context.IndexInventory[index];
            var updatedIndex = ApplyToIndex(entry, dialectRules, out var changed);

            if (!changed)
            {
                continue;
            }

            context.IndexInventory[index] = updatedIndex;
        }

        for (var index = 0; index < context.TriggerInventory.Count; index++)
        {
            var entry = context.TriggerInventory[index];
            var updatedTrigger = ApplyToTrigger(entry, dialectRules, out var changed);

            if (!changed)
            {
                continue;
            }

            context.TriggerInventory[index] = updatedTrigger;
        }
    }

    /// <summary>
    /// Updates project schema names using dialect shortening rules.
    /// </summary>
    private static void UpdateProjectSchemas(
        RelationalModelSetBuilderContext context,
        ISqlDialectRules dialectRules
    )
    {
        context.UpdateProjectSchemasInEndpointOrder(schema =>
        {
            var shortened = ShortenSchema(schema.PhysicalSchema, dialectRules);

            return shortened.Equals(schema.PhysicalSchema)
                ? schema
                : schema with
                {
                    PhysicalSchema = shortened,
                };
        });
    }

    /// <summary>
    /// Applies dialect shortening to a single resource model and reports whether any identifiers changed.
    /// </summary>
    private static RelationalResourceModel ApplyToResource(
        RelationalResourceModel resourceModel,
        ISqlDialectRules dialectRules,
        out bool changed
    )
    {
        changed = false;

        var updatedPhysicalSchema = ShortenSchema(resourceModel.PhysicalSchema, dialectRules);

        if (!updatedPhysicalSchema.Equals(resourceModel.PhysicalSchema))
        {
            changed = true;
        }

        var updatedTables = new DbTableModel[resourceModel.TablesInDependencyOrder.Count];

        for (var index = 0; index < resourceModel.TablesInDependencyOrder.Count; index++)
        {
            var table = resourceModel.TablesInDependencyOrder[index];
            updatedTables[index] = ApplyToTable(table, dialectRules, out var tableChanged);
            changed |= tableChanged;
        }

        var updatedReferences = new DocumentReferenceBinding[resourceModel.DocumentReferenceBindings.Count];

        for (var index = 0; index < resourceModel.DocumentReferenceBindings.Count; index++)
        {
            var binding = resourceModel.DocumentReferenceBindings[index];
            updatedReferences[index] = ApplyToBinding(binding, dialectRules, out var bindingChanged);
            changed |= bindingChanged;
        }

        var updatedDescriptorEdges = new DescriptorEdgeSource[resourceModel.DescriptorEdgeSources.Count];

        for (var index = 0; index < resourceModel.DescriptorEdgeSources.Count; index++)
        {
            var edge = resourceModel.DescriptorEdgeSources[index];
            updatedDescriptorEdges[index] = ApplyToDescriptorEdge(edge, dialectRules, out var edgeChanged);
            changed |= edgeChanged;
        }

        if (!changed)
        {
            return resourceModel;
        }

        var rootScope = resourceModel.Root.JsonScope;
        var updatedRoot = updatedTables.Single(table => table.JsonScope.Equals(rootScope));

        return resourceModel with
        {
            PhysicalSchema = updatedPhysicalSchema,
            Root = updatedRoot,
            TablesInDependencyOrder = updatedTables,
            DocumentReferenceBindings = updatedReferences,
            DescriptorEdgeSources = updatedDescriptorEdges,
        };
    }

    /// <summary>
    /// Applies dialect shortening to a single table model and reports whether any identifiers changed.
    /// </summary>
    private static DbTableModel ApplyToTable(
        DbTableModel table,
        ISqlDialectRules dialectRules,
        out bool changed
    )
    {
        changed = false;

        var updatedTableName = ShortenTable(table.Table, dialectRules);

        if (!updatedTableName.Equals(table.Table))
        {
            changed = true;
        }

        var updatedKey = ApplyToKey(table.Table, table.Key, dialectRules, out var keyChanged);
        changed |= keyChanged;

        var updatedColumns = new DbColumnModel[table.Columns.Count];

        for (var index = 0; index < table.Columns.Count; index++)
        {
            var column = table.Columns[index];
            updatedColumns[index] = ApplyToColumn(column, dialectRules, out var columnChanged);
            changed |= columnChanged;
        }

        var updatedConstraints = new TableConstraint[table.Constraints.Count];

        for (var index = 0; index < table.Constraints.Count; index++)
        {
            var constraint = table.Constraints[index];
            updatedConstraints[index] = ApplyToConstraint(
                constraint,
                dialectRules,
                out var constraintChanged
            );
            changed |= constraintChanged;
        }

        if (!changed)
        {
            return table;
        }

        return table with
        {
            Table = updatedTableName,
            Key = updatedKey,
            Columns = updatedColumns,
            Constraints = updatedConstraints,
        };
    }

    /// <summary>
    /// Applies dialect shortening to a table key (constraint name and key column names) and reports whether it changed.
    /// </summary>
    private static TableKey ApplyToKey(
        DbTableName table,
        TableKey key,
        ISqlDialectRules dialectRules,
        out bool changed
    )
    {
        changed = false;
        var keyName = string.IsNullOrWhiteSpace(key.ConstraintName)
            ? ConstraintNaming.BuildPrimaryKeyName(table)
            : key.ConstraintName;
        var updatedConstraintName = dialectRules.ShortenIdentifier(keyName);

        if (!string.Equals(updatedConstraintName, key.ConstraintName, StringComparison.Ordinal))
        {
            changed = true;
        }

        var updatedColumns = new DbKeyColumn[key.Columns.Count];

        for (var index = 0; index < key.Columns.Count; index++)
        {
            var column = key.Columns[index];
            var updatedName = ShortenColumn(column.ColumnName, dialectRules);

            if (!updatedName.Equals(column.ColumnName))
            {
                changed = true;
            }

            updatedColumns[index] = column with { ColumnName = updatedName };
        }

        if (!changed)
        {
            return key;
        }

        return key with
        {
            ConstraintName = updatedConstraintName,
            Columns = updatedColumns,
        };
    }

    /// <summary>
    /// Applies dialect shortening to a single column model and reports whether it changed.
    /// </summary>
    private static DbColumnModel ApplyToColumn(
        DbColumnModel column,
        ISqlDialectRules dialectRules,
        out bool changed
    )
    {
        var updatedName = ShortenColumn(column.ColumnName, dialectRules);
        changed = !updatedName.Equals(column.ColumnName);

        if (!changed)
        {
            return column;
        }

        return column with
        {
            ColumnName = updatedName,
        };
    }

    /// <summary>
    /// Applies dialect shortening to a table constraint and reports whether it changed.
    /// </summary>
    private static TableConstraint ApplyToConstraint(
        TableConstraint constraint,
        ISqlDialectRules dialectRules,
        out bool changed
    )
    {
        changed = false;

        switch (constraint)
        {
            case TableConstraint.Unique unique:
            {
                var updatedName = dialectRules.ShortenIdentifier(unique.Name);
                var updatedColumns = ShortenColumns(unique.Columns, dialectRules, out var columnsChanged);
                changed =
                    columnsChanged || !string.Equals(updatedName, unique.Name, StringComparison.Ordinal);

                if (!changed)
                {
                    return unique;
                }

                return unique with
                {
                    Name = updatedName,
                    Columns = updatedColumns,
                };
            }
            case TableConstraint.ForeignKey foreignKey:
            {
                var updatedName = dialectRules.ShortenIdentifier(foreignKey.Name);
                var updatedColumns = ShortenColumns(foreignKey.Columns, dialectRules, out var columnsChanged);
                var updatedTargetColumns = ShortenColumns(
                    foreignKey.TargetColumns,
                    dialectRules,
                    out var targetChanged
                );
                var updatedTargetTable = ShortenTable(foreignKey.TargetTable, dialectRules);

                changed =
                    columnsChanged
                    || targetChanged
                    || !updatedTargetTable.Equals(foreignKey.TargetTable)
                    || !string.Equals(updatedName, foreignKey.Name, StringComparison.Ordinal);

                if (!changed)
                {
                    return foreignKey;
                }

                return foreignKey with
                {
                    Name = updatedName,
                    Columns = updatedColumns,
                    TargetTable = updatedTargetTable,
                    TargetColumns = updatedTargetColumns,
                };
            }
            case TableConstraint.AllOrNoneNullability allOrNone:
            {
                var updatedName = dialectRules.ShortenIdentifier(allOrNone.Name);
                var updatedFk = ShortenColumn(allOrNone.FkColumn, dialectRules);
                var updatedDependents = ShortenColumns(
                    allOrNone.DependentColumns,
                    dialectRules,
                    out var dependentsChanged
                );

                changed =
                    dependentsChanged
                    || !updatedFk.Equals(allOrNone.FkColumn)
                    || !string.Equals(updatedName, allOrNone.Name, StringComparison.Ordinal);

                if (!changed)
                {
                    return allOrNone;
                }

                return allOrNone with
                {
                    Name = updatedName,
                    FkColumn = updatedFk,
                    DependentColumns = updatedDependents,
                };
            }
            default:
                throw new InvalidOperationException(
                    $"Unsupported constraint type '{constraint.GetType().Name}'."
                );
        }
    }

    /// <summary>
    /// Applies dialect shortening to a column name collection and reports whether any element changed.
    /// </summary>
    private static IReadOnlyList<DbColumnName> ShortenColumns(
        IReadOnlyList<DbColumnName> columns,
        ISqlDialectRules dialectRules,
        out bool changed
    )
    {
        changed = false;
        var updatedColumns = new DbColumnName[columns.Count];

        for (var index = 0; index < columns.Count; index++)
        {
            var column = columns[index];
            var updated = ShortenColumn(column, dialectRules);

            if (!updated.Equals(column))
            {
                changed = true;
            }

            updatedColumns[index] = updated;
        }

        return changed ? updatedColumns : columns;
    }

    /// <summary>
    /// Applies dialect shortening to a document reference binding and reports whether it changed.
    /// </summary>
    private static DocumentReferenceBinding ApplyToBinding(
        DocumentReferenceBinding binding,
        ISqlDialectRules dialectRules,
        out bool changed
    )
    {
        changed = false;

        var updatedTable = ShortenTable(binding.Table, dialectRules);
        var updatedFkColumn = ShortenColumn(binding.FkColumn, dialectRules);

        if (!updatedTable.Equals(binding.Table) || !updatedFkColumn.Equals(binding.FkColumn))
        {
            changed = true;
        }

        var updatedIdentityBindings = new ReferenceIdentityBinding[binding.IdentityBindings.Count];

        for (var index = 0; index < binding.IdentityBindings.Count; index++)
        {
            var identity = binding.IdentityBindings[index];
            var updatedColumn = ShortenColumn(identity.Column, dialectRules);

            if (!updatedColumn.Equals(identity.Column))
            {
                changed = true;
            }

            updatedIdentityBindings[index] = identity with { Column = updatedColumn };
        }

        if (!changed)
        {
            return binding;
        }

        return binding with
        {
            Table = updatedTable,
            FkColumn = updatedFkColumn,
            IdentityBindings = updatedIdentityBindings,
        };
    }

    /// <summary>
    /// Applies dialect shortening to a descriptor edge source and reports whether it changed.
    /// </summary>
    private static DescriptorEdgeSource ApplyToDescriptorEdge(
        DescriptorEdgeSource edge,
        ISqlDialectRules dialectRules,
        out bool changed
    )
    {
        var updatedTable = ShortenTable(edge.Table, dialectRules);
        var updatedFk = ShortenColumn(edge.FkColumn, dialectRules);
        changed = !updatedTable.Equals(edge.Table) || !updatedFk.Equals(edge.FkColumn);

        if (!changed)
        {
            return edge;
        }

        return edge with
        {
            Table = updatedTable,
            FkColumn = updatedFk,
        };
    }

    /// <summary>
    /// Applies dialect shortening to an abstract union view and reports whether it changed.
    /// </summary>
    private static AbstractUnionViewInfo ApplyToUnionView(
        AbstractUnionViewInfo view,
        ISqlDialectRules dialectRules,
        out bool changed
    )
    {
        changed = false;
        var updatedViewName = ShortenTable(view.ViewName, dialectRules);

        if (!updatedViewName.Equals(view.ViewName))
        {
            changed = true;
        }

        var updatedColumns = new AbstractUnionViewOutputColumn[view.OutputColumnsInSelectOrder.Count];

        for (var index = 0; index < view.OutputColumnsInSelectOrder.Count; index++)
        {
            var column = view.OutputColumnsInSelectOrder[index];
            updatedColumns[index] = ApplyToUnionViewOutputColumn(column, dialectRules, out var columnChanged);
            changed |= columnChanged;
        }

        var updatedArms = new AbstractUnionViewArm[view.UnionArmsInOrder.Count];

        for (var index = 0; index < view.UnionArmsInOrder.Count; index++)
        {
            var arm = view.UnionArmsInOrder[index];
            updatedArms[index] = ApplyToUnionViewArm(arm, dialectRules, out var armChanged);
            changed |= armChanged;
        }

        if (!changed)
        {
            return view;
        }

        return view with
        {
            ViewName = updatedViewName,
            OutputColumnsInSelectOrder = updatedColumns,
            UnionArmsInOrder = updatedArms,
        };
    }

    /// <summary>
    /// Applies dialect shortening to an abstract union-view output column and reports whether it changed.
    /// </summary>
    private static AbstractUnionViewOutputColumn ApplyToUnionViewOutputColumn(
        AbstractUnionViewOutputColumn column,
        ISqlDialectRules dialectRules,
        out bool changed
    )
    {
        var updatedColumnName = ShortenColumn(column.ColumnName, dialectRules);
        changed = !updatedColumnName.Equals(column.ColumnName);

        if (!changed)
        {
            return column;
        }

        return column with
        {
            ColumnName = updatedColumnName,
        };
    }

    /// <summary>
    /// Applies dialect shortening to an abstract union-view arm and reports whether it changed.
    /// </summary>
    private static AbstractUnionViewArm ApplyToUnionViewArm(
        AbstractUnionViewArm arm,
        ISqlDialectRules dialectRules,
        out bool changed
    )
    {
        changed = false;
        var updatedFromTable = ShortenTable(arm.FromTable, dialectRules);

        if (!updatedFromTable.Equals(arm.FromTable))
        {
            changed = true;
        }

        var updatedProjections = new AbstractUnionViewProjectionExpression[
            arm.ProjectionExpressionsInSelectOrder.Count
        ];

        for (var index = 0; index < arm.ProjectionExpressionsInSelectOrder.Count; index++)
        {
            var expression = arm.ProjectionExpressionsInSelectOrder[index];
            updatedProjections[index] = ApplyToUnionViewProjection(
                expression,
                dialectRules,
                out var expressionChanged
            );
            changed |= expressionChanged;
        }

        if (!changed)
        {
            return arm;
        }

        return arm with
        {
            FromTable = updatedFromTable,
            ProjectionExpressionsInSelectOrder = updatedProjections,
        };
    }

    /// <summary>
    /// Applies dialect shortening to an abstract union-view projection expression and reports whether it changed.
    /// </summary>
    private static AbstractUnionViewProjectionExpression ApplyToUnionViewProjection(
        AbstractUnionViewProjectionExpression expression,
        ISqlDialectRules dialectRules,
        out bool changed
    )
    {
        switch (expression)
        {
            case AbstractUnionViewProjectionExpression.SourceColumn sourceColumn:
            {
                var updatedColumn = ShortenColumn(sourceColumn.ColumnName, dialectRules);
                changed = !updatedColumn.Equals(sourceColumn.ColumnName);

                if (!changed)
                {
                    return sourceColumn;
                }

                return sourceColumn with
                {
                    ColumnName = updatedColumn,
                };
            }
            case AbstractUnionViewProjectionExpression.StringLiteral:
                changed = false;
                return expression;
            default:
                throw new InvalidOperationException(
                    $"Unsupported abstract union-view projection expression '{expression.GetType().Name}'."
                );
        }
    }

    /// <summary>
    /// Applies dialect shortening to an index and reports whether it changed.
    /// </summary>
    private static DbIndexInfo ApplyToIndex(
        DbIndexInfo index,
        ISqlDialectRules dialectRules,
        out bool changed
    )
    {
        var updatedName = new DbIndexName(dialectRules.ShortenIdentifier(index.Name.Value));
        var updatedTable = ShortenTable(index.Table, dialectRules);
        var updatedColumns = ShortenColumns(index.KeyColumns, dialectRules, out var columnsChanged);

        changed = columnsChanged || !updatedTable.Equals(index.Table) || !updatedName.Equals(index.Name);

        if (!changed)
        {
            return index;
        }

        return index with
        {
            Name = updatedName,
            Table = updatedTable,
            KeyColumns = updatedColumns,
        };
    }

    /// <summary>
    /// Applies dialect shortening to a trigger and reports whether it changed.
    /// </summary>
    private static DbTriggerInfo ApplyToTrigger(
        DbTriggerInfo trigger,
        ISqlDialectRules dialectRules,
        out bool changed
    )
    {
        var updatedName = new DbTriggerName(dialectRules.ShortenIdentifier(trigger.Name.Value));
        var updatedTable = ShortenTable(trigger.Table, dialectRules);
        var updatedColumns = ShortenColumns(trigger.KeyColumns, dialectRules, out var columnsChanged);
        var updatedIdentityColumns = ShortenColumns(
            trigger.IdentityProjectionColumns,
            dialectRules,
            out var identityColumnsChanged
        );
        var updatedParameters = ApplyToTriggerParameters(
            trigger.Parameters,
            dialectRules,
            out var parametersChanged
        );

        changed =
            columnsChanged
            || identityColumnsChanged
            || parametersChanged
            || !updatedTable.Equals(trigger.Table)
            || !updatedName.Equals(trigger.Name);

        if (!changed)
        {
            return trigger;
        }

        return trigger with
        {
            Name = updatedName,
            Table = updatedTable,
            KeyColumns = updatedColumns,
            IdentityProjectionColumns = updatedIdentityColumns,
            Parameters = updatedParameters,
        };
    }

    /// <summary>
    /// Applies dialect shortening to trigger-kind-specific parameters and reports whether they changed.
    /// </summary>
    private static TriggerKindParameters ApplyToTriggerParameters(
        TriggerKindParameters parameters,
        ISqlDialectRules dialectRules,
        out bool changed
    )
    {
        switch (parameters)
        {
            case TriggerKindParameters.ReferentialIdentityMaintenance refId:
            {
                var updatedElements = ShortenIdentityElementMappings(
                    refId.IdentityElements,
                    dialectRules,
                    out var elementsChanged
                );
                var updatedAlias = ShortenSuperclassAlias(
                    refId.SuperclassAlias,
                    dialectRules,
                    out var aliasChanged
                );
                changed = elementsChanged || aliasChanged;
                return changed
                    ? refId with
                    {
                        IdentityElements = updatedElements,
                        SuperclassAlias = updatedAlias,
                    }
                    : parameters;
            }
            case TriggerKindParameters.AbstractIdentityMaintenance abstractId:
            {
                var updatedTargetTable = ShortenTable(abstractId.TargetTable, dialectRules);
                var updatedMappings = ShortenTriggerColumnMappings(
                    abstractId.TargetColumnMappings,
                    dialectRules,
                    out var mappingsChanged
                );
                changed = mappingsChanged || !updatedTargetTable.Equals(abstractId.TargetTable);
                return changed
                    ? abstractId with
                    {
                        TargetTable = updatedTargetTable,
                        TargetColumnMappings = updatedMappings,
                    }
                    : parameters;
            }
            case TriggerKindParameters.IdentityPropagationFallback propagation:
            {
                var updatedTargetTable = ShortenTable(propagation.TargetTable, dialectRules);
                var updatedMappings = ShortenTriggerColumnMappings(
                    propagation.TargetColumnMappings,
                    dialectRules,
                    out var mappingsChanged
                );
                changed = mappingsChanged || !updatedTargetTable.Equals(propagation.TargetTable);
                return changed
                    ? propagation with
                    {
                        TargetTable = updatedTargetTable,
                        TargetColumnMappings = updatedMappings,
                    }
                    : parameters;
            }
            case TriggerKindParameters.DocumentStamping:
                changed = false;
                return parameters;
            default:
                throw new InvalidOperationException(
                    $"Unsupported trigger kind parameters type '{parameters.GetType().Name}'."
                );
        }
    }

    /// <summary>
    /// Shortens column names in trigger column mappings using dialect rules.
    /// </summary>
    private static IReadOnlyList<TriggerColumnMapping> ShortenTriggerColumnMappings(
        IReadOnlyList<TriggerColumnMapping> mappings,
        ISqlDialectRules dialectRules,
        out bool changed
    )
    {
        changed = false;
        var updated = new TriggerColumnMapping[mappings.Count];

        for (var i = 0; i < mappings.Count; i++)
        {
            var mapping = mappings[i];
            var updatedSource = ShortenColumn(mapping.SourceColumn, dialectRules);
            var updatedTarget = ShortenColumn(mapping.TargetColumn, dialectRules);

            if (!updatedSource.Equals(mapping.SourceColumn) || !updatedTarget.Equals(mapping.TargetColumn))
            {
                changed = true;
            }

            updated[i] = new TriggerColumnMapping(updatedSource, updatedTarget);
        }

        return changed ? updated : mappings;
    }

    /// <summary>
    /// Shortens column names in identity element mappings using dialect rules.
    /// </summary>
    private static IReadOnlyList<IdentityElementMapping> ShortenIdentityElementMappings(
        IReadOnlyList<IdentityElementMapping> elements,
        ISqlDialectRules dialectRules,
        out bool changed
    )
    {
        changed = false;
        var updated = new IdentityElementMapping[elements.Count];

        for (var i = 0; i < elements.Count; i++)
        {
            var element = elements[i];
            var updatedColumn = ShortenColumn(element.Column, dialectRules);

            if (!updatedColumn.Equals(element.Column))
            {
                changed = true;
            }

            updated[i] = element with { Column = updatedColumn };
        }

        return changed ? updated : elements;
    }

    /// <summary>
    /// Shortens column names in a superclass alias's identity elements using dialect rules.
    /// </summary>
    private static SuperclassAliasInfo? ShortenSuperclassAlias(
        SuperclassAliasInfo? alias,
        ISqlDialectRules dialectRules,
        out bool changed
    )
    {
        if (alias is null)
        {
            changed = false;
            return null;
        }

        var updatedElements = ShortenIdentityElementMappings(
            alias.IdentityElements,
            dialectRules,
            out changed
        );

        return changed ? alias with { IdentityElements = updatedElements } : alias;
    }

    /// <summary>
    /// Shortens a schema identifier using dialect rules.
    /// </summary>
    private static DbSchemaName ShortenSchema(DbSchemaName schema, ISqlDialectRules dialectRules)
    {
        var shortened = dialectRules.ShortenIdentifier(schema.Value);
        return string.Equals(shortened, schema.Value, StringComparison.Ordinal)
            ? schema
            : new DbSchemaName(shortened);
    }

    /// <summary>
    /// Shortens a schema-qualified table identifier using dialect rules.
    /// </summary>
    private static DbTableName ShortenTable(DbTableName table, ISqlDialectRules dialectRules)
    {
        var updatedSchema = ShortenSchema(table.Schema, dialectRules);
        var updatedName = dialectRules.ShortenIdentifier(table.Name);

        if (
            updatedSchema.Equals(table.Schema)
            && string.Equals(updatedName, table.Name, StringComparison.Ordinal)
        )
        {
            return table;
        }

        return new DbTableName(updatedSchema, updatedName);
    }

    /// <summary>
    /// Shortens a column identifier using dialect rules.
    /// </summary>
    private static DbColumnName ShortenColumn(DbColumnName column, ISqlDialectRules dialectRules)
    {
        var updated = dialectRules.ShortenIdentifier(column.Value);
        return string.Equals(updated, column.Value, StringComparison.Ordinal)
            ? column
            : new DbColumnName(updated);
    }

    /// <summary>
    /// Validates that dialect shortening does not introduce collisions across derived identifiers.
    /// </summary>
    private static void ValidateIdentifierShorteningCollisions(RelationalModelSetBuilderContext context)
    {
        var detector = new IdentifierCollisionDetector(
            context.DialectRules,
            IdentifierCollisionStage.AfterDialectShortening(context.DialectRules)
        );

        RegisterSchemaCollisions(context, detector);

        // ConcreteResourcesInNameOrder, AbstractIdentityTablesInNameOrder, and
        // AbstractUnionViewsInNameOrder are already maintained in (ProjectName, ResourceName)
        // order by construction, so no re-sorting is needed.

        var canonicalIndexes = context
            .IndexInventory.OrderBy(index => index.Table.Schema.Value, StringComparer.Ordinal)
            .ThenBy(index => index.Table.Name, StringComparer.Ordinal)
            .ThenBy(index => index.Name.Value, StringComparer.Ordinal)
            .ToArray();

        var canonicalTriggers = context
            .TriggerInventory.OrderBy(trigger => trigger.Table.Schema.Value, StringComparer.Ordinal)
            .ThenBy(trigger => trigger.Table.Name, StringComparer.Ordinal)
            .ThenBy(trigger => trigger.Name.Value, StringComparer.Ordinal)
            .ToArray();
        var registeredTables = new HashSet<DbTableName>();
        var registeredColumns = new HashSet<(DbTableName Table, DbColumnName Column)>();

        foreach (var resource in context.ConcreteResourcesInNameOrder)
        {
            var resourceLabel = FormatResource(resource.ResourceKey.Resource);

            foreach (var table in resource.RelationalModel.TablesInDependencyOrder)
            {
                detector.RegisterTable(
                    table.Table,
                    BuildOrigin($"table {FormatTable(table.Table)}", resourceLabel, table.JsonScope)
                );
                registeredTables.Add(table.Table);

                foreach (var column in table.Columns)
                {
                    detector.RegisterColumn(
                        table.Table,
                        column.ColumnName,
                        BuildOrigin(
                            $"column {FormatColumn(table.Table, column.ColumnName)}",
                            resourceLabel,
                            column.SourceJsonPath,
                            table.JsonScope
                        )
                    );
                    registeredColumns.Add((table.Table, column.ColumnName));
                }

                var primaryKeyConstraintName = ResolvePrimaryKeyConstraintName(table.Table, table.Key);

                detector.RegisterConstraint(
                    table.Table,
                    primaryKeyConstraintName,
                    BuildOrigin(
                        $"primary key constraint {primaryKeyConstraintName} on {FormatTable(table.Table)}",
                        resourceLabel,
                        null,
                        table.JsonScope
                    )
                );

                foreach (var constraint in table.Constraints)
                {
                    var constraintName = GetConstraintName(constraint);

                    detector.RegisterConstraint(
                        table.Table,
                        constraintName,
                        BuildOrigin(
                            $"constraint {constraintName} on {FormatTable(table.Table)}",
                            resourceLabel,
                            null
                        )
                    );
                }
            }
        }

        foreach (var table in context.AbstractIdentityTablesInNameOrder)
        {
            var resourceLabel = FormatResource(table.AbstractResourceKey.Resource);
            var tableModel = table.TableModel;

            detector.RegisterTable(
                tableModel.Table,
                BuildOrigin(
                    $"table {FormatTable(tableModel.Table)} (abstract identity)",
                    resourceLabel,
                    tableModel.JsonScope
                )
            );
            registeredTables.Add(tableModel.Table);

            foreach (var column in tableModel.Columns)
            {
                detector.RegisterColumn(
                    tableModel.Table,
                    column.ColumnName,
                    BuildOrigin(
                        $"column {FormatColumn(tableModel.Table, column.ColumnName)} (abstract identity)",
                        resourceLabel,
                        column.SourceJsonPath,
                        tableModel.JsonScope
                    )
                );
                registeredColumns.Add((tableModel.Table, column.ColumnName));
            }

            var primaryKeyConstraintName = ResolvePrimaryKeyConstraintName(tableModel.Table, tableModel.Key);

            detector.RegisterConstraint(
                tableModel.Table,
                primaryKeyConstraintName,
                BuildOrigin(
                    $"primary key constraint {primaryKeyConstraintName} on {FormatTable(tableModel.Table)} (abstract identity)",
                    resourceLabel,
                    null,
                    tableModel.JsonScope
                )
            );

            foreach (var constraint in tableModel.Constraints)
            {
                var constraintName = GetConstraintName(constraint);

                detector.RegisterConstraint(
                    tableModel.Table,
                    constraintName,
                    BuildOrigin(
                        $"constraint {constraintName} on {FormatTable(tableModel.Table)} (abstract identity)",
                        resourceLabel,
                        null
                    )
                );
            }
        }

        foreach (var view in context.AbstractUnionViewsInNameOrder)
        {
            var resourceLabel = FormatResource(view.AbstractResourceKey.Resource);

            detector.RegisterTable(
                view.ViewName,
                BuildOrigin($"view {FormatTable(view.ViewName)} (abstract union)", resourceLabel, null)
            );
            registeredTables.Add(view.ViewName);

            foreach (var column in view.OutputColumnsInSelectOrder)
            {
                detector.RegisterColumn(
                    view.ViewName,
                    column.ColumnName,
                    BuildOrigin(
                        $"column {FormatColumn(view.ViewName, column.ColumnName)} (abstract union)",
                        resourceLabel,
                        column.SourceJsonPath
                    )
                );
                registeredColumns.Add((view.ViewName, column.ColumnName));
            }

            foreach (var arm in view.UnionArmsInOrder)
            {
                RegisterUnionArmSourceIdentifierCollisions(
                    detector,
                    arm,
                    registeredTables,
                    registeredColumns
                );
            }
        }

        foreach (var index in canonicalIndexes)
        {
            detector.RegisterIndex(
                index.Table,
                index.Name,
                BuildOrigin($"index {index.Name.Value} on {FormatTable(index.Table)}", null, null)
            );
        }

        foreach (var trigger in canonicalTriggers)
        {
            detector.RegisterTrigger(
                trigger.Table,
                trigger.Name,
                BuildOrigin($"trigger {trigger.Name.Value} on {FormatTable(trigger.Table)}", null, null)
            );
        }

        detector.ThrowIfCollisions();
    }

    /// <summary>
    /// Registers source identifiers referenced by abstract union-view arms when not already covered by
    /// concrete/abstract table registrations.
    /// </summary>
    private static void RegisterUnionArmSourceIdentifierCollisions(
        IdentifierCollisionDetector detector,
        AbstractUnionViewArm arm,
        HashSet<DbTableName> registeredTables,
        HashSet<(DbTableName Table, DbColumnName Column)> registeredColumns
    )
    {
        var resourceLabel = FormatResource(arm.ConcreteMemberResourceKey.Resource);

        if (registeredTables.Add(arm.FromTable))
        {
            detector.RegisterTable(
                arm.FromTable,
                BuildOrigin(
                    $"table {FormatTable(arm.FromTable)} (abstract union arm source)",
                    resourceLabel,
                    null
                )
            );
        }

        foreach (var projection in arm.ProjectionExpressionsInSelectOrder)
        {
            if (projection is not AbstractUnionViewProjectionExpression.SourceColumn sourceColumn)
            {
                continue;
            }

            var columnKey = (arm.FromTable, sourceColumn.ColumnName);

            if (!registeredColumns.Add(columnKey))
            {
                continue;
            }

            detector.RegisterColumn(
                arm.FromTable,
                sourceColumn.ColumnName,
                BuildOrigin(
                    $"column {FormatColumn(arm.FromTable, sourceColumn.ColumnName)} (abstract union arm source)",
                    resourceLabel,
                    null
                )
            );
        }
    }

    /// <summary>
    /// Registers schema identifiers that may be produced by derived models for collision detection.
    /// </summary>
    private static void RegisterSchemaCollisions(
        RelationalModelSetBuilderContext context,
        IdentifierCollisionDetector detector
    )
    {
        Dictionary<string, IdentifierCollisionOrigin> schemaOrigins = new(StringComparer.Ordinal);

        foreach (var project in context.ProjectSchemasInEndpointOrder)
        {
            schemaOrigins[project.PhysicalSchema.Value] = new IdentifierCollisionOrigin(
                $"project schema '{project.ProjectEndpointName}'",
                null,
                null
            );
        }

        foreach (var resource in context.ConcreteResourcesInNameOrder)
        {
            var resourceLabel = FormatResource(resource.ResourceKey.Resource);

            foreach (var table in resource.RelationalModel.TablesInDependencyOrder)
            {
                var schema = table.Table.Schema.Value;

                if (schemaOrigins.ContainsKey(schema))
                {
                    continue;
                }

                schemaOrigins[schema] = BuildOrigin(
                    $"table {FormatTable(table.Table)}",
                    resourceLabel,
                    table.JsonScope
                );
            }
        }

        foreach (var table in context.AbstractIdentityTablesInNameOrder)
        {
            var schema = table.TableModel.Table.Schema.Value;

            if (schemaOrigins.ContainsKey(schema))
            {
                continue;
            }

            schemaOrigins[schema] = BuildOrigin(
                $"table {FormatTable(table.TableModel.Table)} (abstract identity)",
                FormatResource(table.AbstractResourceKey.Resource),
                table.TableModel.JsonScope
            );
        }

        foreach (var view in context.AbstractUnionViewsInNameOrder)
        {
            var schema = view.ViewName.Schema.Value;

            if (schemaOrigins.ContainsKey(schema))
            {
                continue;
            }

            schemaOrigins[schema] = BuildOrigin(
                $"view {FormatTable(view.ViewName)} (abstract union)",
                FormatResource(view.AbstractResourceKey.Resource),
                null
            );
        }

        foreach (var entry in schemaOrigins.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            detector.RegisterSchema(new DbSchemaName(entry.Key), entry.Value);
        }
    }

    /// <summary>
    /// Builds an <see cref="IdentifierCollisionOrigin"/> for a derived identifier registration.
    /// </summary>
    private static IdentifierCollisionOrigin BuildOrigin(
        string description,
        string? resourceLabel,
        JsonPathExpression? sourcePath,
        JsonPathExpression? fallbackScope = null
    )
    {
        var resolvedPath = sourcePath ?? fallbackScope;
        return new IdentifierCollisionOrigin(description, resourceLabel, resolvedPath?.Canonical);
    }

    /// <summary>
    /// Formats a table name for diagnostics.
    /// </summary>
    private static string FormatTable(DbTableName table)
    {
        return $"{table.Schema.Value}.{table.Name}";
    }

    /// <summary>
    /// Formats a column name for diagnostics.
    /// </summary>
    private static string FormatColumn(DbTableName table, DbColumnName column)
    {
        return $"{FormatTable(table)}.{column.Value}";
    }

    /// <summary>
    /// Resolves a primary key constraint name for collision registration.
    /// </summary>
    private static string ResolvePrimaryKeyConstraintName(DbTableName table, TableKey key)
    {
        return string.IsNullOrWhiteSpace(key.ConstraintName)
            ? ConstraintNaming.BuildPrimaryKeyName(table)
            : key.ConstraintName;
    }

    /// <summary>
    /// Extracts a name from the constraint for collision registration.
    /// </summary>
    private static string GetConstraintName(TableConstraint constraint)
    {
        return constraint switch
        {
            TableConstraint.Unique unique => unique.Name,
            TableConstraint.ForeignKey foreignKey => foreignKey.Name,
            TableConstraint.AllOrNoneNullability allOrNone => allOrNone.Name,
            _ => throw new InvalidOperationException(
                $"Unsupported constraint type '{constraint.GetType().Name}'."
            ),
        };
    }
}
