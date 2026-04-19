// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Runtime.CompilerServices;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

internal sealed class PageOrderedChildRows
{
    private readonly IReadOnlyDictionary<
        DbTableName,
        IReadOnlyDictionary<long, IReadOnlyList<object?[]>>
    > _rowsByTableAndParentLocator;

    private PageOrderedChildRows(
        IReadOnlyDictionary<
            DbTableName,
            IReadOnlyDictionary<long, IReadOnlyList<object?[]>>
        > rowsByTableAndParentLocator
    )
    {
        ArgumentNullException.ThrowIfNull(rowsByTableAndParentLocator);

        _rowsByTableAndParentLocator = rowsByTableAndParentLocator;
    }

    public IReadOnlyList<object?[]> GetRowsByParentLocator(DbTableName childTable, long parentLocatorValue) =>
        _rowsByTableAndParentLocator.TryGetValue(childTable, out var rowsByParentLocator)
        && rowsByParentLocator.TryGetValue(parentLocatorValue, out var rows)
            ? rows
            : Array.Empty<object?[]>();

    public static PageOrderedChildRows Build(
        CompiledReconstitutionPlan compiledPlan,
        IReadOnlyList<HydratedTableRows> tableRowsInDependencyOrder
    )
    {
        ArgumentNullException.ThrowIfNull(compiledPlan);
        ArgumentNullException.ThrowIfNull(tableRowsInDependencyOrder);

        Dictionary<
            DbTableName,
            IReadOnlyDictionary<long, IReadOnlyList<object?[]>>
        > rowsByTableAndParentLocator = [];

        foreach (var tableRows in tableRowsInDependencyOrder)
        {
            var tablePlan = compiledPlan.GetTablePlanOrThrow(tableRows.TableModel.Table);

            if (tablePlan.ImmediateParentScopeLocatorOrdinals.IsDefaultOrEmpty)
            {
                continue;
            }

            rowsByTableAndParentLocator.Add(
                tableRows.TableModel.Table,
                BuildRowsByParentLocator(
                    tableRows.Rows,
                    tablePlan.ResolveSingleImmediateParentScopeLocatorOrdinalOrThrow(),
                    tablePlan.OrdinalColumnOrdinal
                )
            );
        }

        return new PageOrderedChildRows(rowsByTableAndParentLocator);
    }

    private static IReadOnlyDictionary<long, IReadOnlyList<object?[]>> BuildRowsByParentLocator(
        IReadOnlyList<object?[]> rows,
        int parentLocatorOrdinal,
        int? ordinalColumnOrdinal
    )
    {
        Dictionary<long, List<object?[]>> rowsByParentLocator = [];

        foreach (var row in rows)
        {
            var parentLocatorValue = Convert.ToInt64(row[parentLocatorOrdinal]);

            if (!rowsByParentLocator.TryGetValue(parentLocatorValue, out var childRows))
            {
                childRows = [];
                rowsByParentLocator[parentLocatorValue] = childRows;
            }

            childRows.Add(row);
        }

        if (ordinalColumnOrdinal is not null)
        {
            foreach (var childRows in rowsByParentLocator.Values)
            {
                HydratedRowOrdering.EnsureOrdinalOrder(
                    childRows,
                    row => Convert.ToInt32(row[ordinalColumnOrdinal.Value])
                );
            }
        }

        return rowsByParentLocator.ToDictionary(
            static entry => entry.Key,
            static entry => (IReadOnlyList<object?[]>)entry.Value
        );
    }
}

internal static class PageOrderedChildRowsCache
{
    private static readonly ConditionalWeakTable<
        CompiledReconstitutionPlan,
        ConditionalWeakTable<IReadOnlyList<HydratedTableRows>, PageOrderedChildRows>
    > CacheByPlan = new();

    public static PageOrderedChildRows GetOrBuild(
        CompiledReconstitutionPlan compiledPlan,
        IReadOnlyList<HydratedTableRows> tableRowsInDependencyOrder
    )
    {
        ArgumentNullException.ThrowIfNull(compiledPlan);
        ArgumentNullException.ThrowIfNull(tableRowsInDependencyOrder);

        return CacheByPlan
            .GetValue(
                compiledPlan,
                static _ => new ConditionalWeakTable<IReadOnlyList<HydratedTableRows>, PageOrderedChildRows>()
            )
            .GetValue(tableRowsInDependencyOrder, rows => PageOrderedChildRows.Build(compiledPlan, rows));
    }
}

internal static class HydratedRowOrdering
{
    public static void EnsureOrdinalOrder<T>(List<T> rows, Func<T, int> resolveOrdinal)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(resolveOrdinal);

        if (rows.Count < 2)
        {
            return;
        }

        var previousOrdinal = resolveOrdinal(rows[0]);

        for (var index = 1; index < rows.Count; index++)
        {
            var currentOrdinal = resolveOrdinal(rows[index]);

            if (currentOrdinal < previousOrdinal)
            {
                rows.Sort((left, right) => resolveOrdinal(left).CompareTo(resolveOrdinal(right)));
                return;
            }

            previousOrdinal = currentOrdinal;
        }
    }
}
