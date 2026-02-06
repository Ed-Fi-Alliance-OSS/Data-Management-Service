// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.RelationalModel.SetPasses;

/// <summary>
/// Applies dialect length handling to constraint identifiers by appending a signature hash when required.
/// </summary>
public sealed class ApplyConstraintDialectHashingPass : IRelationalModelSetPass
{
    /// <summary>
    /// Applies dialect shortening for all constraint identifiers in the derived model set.
    /// </summary>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        for (var index = 0; index < context.ConcreteResourcesInNameOrder.Count; index++)
        {
            var entry = context.ConcreteResourcesInNameOrder[index];
            var updatedModel = ApplyToResource(entry.RelationalModel, context.DialectRules);

            if (ReferenceEquals(updatedModel, entry.RelationalModel))
            {
                continue;
            }

            context.ConcreteResourcesInNameOrder[index] = entry with { RelationalModel = updatedModel };
        }

        for (var index = 0; index < context.AbstractIdentityTablesInNameOrder.Count; index++)
        {
            var entry = context.AbstractIdentityTablesInNameOrder[index];
            var updatedTable = ApplyToTable(entry.TableModel, context.DialectRules, out var changed);

            if (!changed)
            {
                continue;
            }

            context.AbstractIdentityTablesInNameOrder[index] = entry with { TableModel = updatedTable };
        }
    }

    private static RelationalResourceModel ApplyToResource(
        RelationalResourceModel resourceModel,
        ISqlDialectRules dialectRules
    )
    {
        var changed = false;
        var updatedTables = resourceModel
            .TablesInDependencyOrder.Select(table =>
            {
                var updatedTable = ApplyToTable(table, dialectRules, out var tableChanged);
                changed |= tableChanged;
                return updatedTable;
            })
            .ToArray();

        if (!changed)
        {
            return resourceModel;
        }

        var updatedRoot = updatedTables.Single(table => table.Table.Equals(resourceModel.Root.Table));

        return resourceModel with
        {
            Root = updatedRoot,
            TablesInDependencyOrder = updatedTables,
        };
    }

    private static DbTableModel ApplyToTable(
        DbTableModel table,
        ISqlDialectRules dialectRules,
        out bool changed
    )
    {
        changed = false;
        var updatedKey = ApplyToPrimaryKey(table.Table, table.Key, dialectRules, out var keyChanged);
        changed |= keyChanged;

        var updatedConstraints = new TableConstraint[table.Constraints.Count];

        for (var index = 0; index < table.Constraints.Count; index++)
        {
            var constraint = table.Constraints[index];
            var updated = ApplyToConstraint(table.Table, constraint, dialectRules, out var updatedChanged);
            updatedConstraints[index] = updated;
            changed |= updatedChanged;
        }

        if (!changed)
        {
            return table;
        }

        return table with
        {
            Key = updatedKey,
            Constraints = updatedConstraints,
        };
    }

    private static TableKey ApplyToPrimaryKey(
        DbTableName table,
        TableKey key,
        ISqlDialectRules dialectRules,
        out bool changed
    )
    {
        var keyName = string.IsNullOrWhiteSpace(key.ConstraintName)
            ? ConstraintNaming.BuildPrimaryKeyName(table)
            : key.ConstraintName;
        var identity = ConstraintIdentity.ForPrimaryKey(
            table,
            key.Columns.Select(column => column.ColumnName).ToArray()
        );
        var updatedName = ConstraintNaming.ApplyDialectLimit(keyName, identity, dialectRules);

        changed = !string.Equals(updatedName, key.ConstraintName, StringComparison.Ordinal);

        if (!changed)
        {
            return key;
        }

        return key with
        {
            ConstraintName = updatedName,
        };
    }

    private static TableConstraint ApplyToConstraint(
        DbTableName table,
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
                var identity = ConstraintIdentity.ForUnique(table, unique.Columns);
                var name = ConstraintNaming.ApplyDialectLimit(unique.Name, identity, dialectRules);

                if (string.Equals(name, unique.Name, StringComparison.Ordinal))
                {
                    return unique;
                }

                changed = true;
                return unique with { Name = name };
            }
            case TableConstraint.ForeignKey foreignKey:
            {
                var identity = ConstraintIdentity.ForForeignKey(
                    table,
                    foreignKey.Columns,
                    foreignKey.TargetTable,
                    foreignKey.TargetColumns,
                    foreignKey.OnDelete,
                    foreignKey.OnUpdate
                );
                var name = ConstraintNaming.ApplyDialectLimit(foreignKey.Name, identity, dialectRules);

                if (string.Equals(name, foreignKey.Name, StringComparison.Ordinal))
                {
                    return foreignKey;
                }

                changed = true;
                return foreignKey with { Name = name };
            }
            case TableConstraint.AllOrNoneNullability allOrNone:
            {
                var identity = ConstraintIdentity.ForAllOrNone(
                    table,
                    allOrNone.FkColumn,
                    allOrNone.DependentColumns
                );
                var name = ConstraintNaming.ApplyDialectLimit(allOrNone.Name, identity, dialectRules);

                if (string.Equals(name, allOrNone.Name, StringComparison.Ordinal))
                {
                    return allOrNone;
                }

                changed = true;
                return allOrNone with { Name = name };
            }
            default:
                throw new InvalidOperationException(
                    $"Unsupported constraint type '{constraint.GetType().Name}'."
                );
        }
    }
}
