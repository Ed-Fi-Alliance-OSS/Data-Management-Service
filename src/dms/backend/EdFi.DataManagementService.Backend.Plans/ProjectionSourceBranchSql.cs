// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// A source table paired with every column it contributes to a projection, in deterministic
/// emission order.
/// </summary>
internal sealed record ProjectionSourceTableGroup(
    DbTableModel TableModel,
    IReadOnlyList<DbColumnName> ColumnsInOrder
);

/// <summary>
/// Emits one page-scoped source branch of a projection's unified id list. The document-reference
/// lookup and descriptor URI projection differ only in which column they project, so both share
/// this emitter.
/// </summary>
/// <remarks>
/// A table contributing several columns is scanned once and its columns expanded through a
/// correlated inline row set, so the keyset join is paid once per source table rather than once per
/// projected column. The non-null predicate then applies to the expanded value, which keeps a row
/// contributing only its populated columns rather than dropping it whenever any column is null.
/// </remarks>
internal static class ProjectionSourceBranchSql
{
    /// <summary>
    /// Groups ordered projection sources by owning table, preserving both the incoming table order
    /// and the incoming column order within each table.
    /// </summary>
    public static IReadOnlyList<ProjectionSourceTableGroup> GroupByTable<TSource>(
        IReadOnlyList<TSource> sourcesInEmissionOrder,
        Func<TSource, DbTableModel> tableModelSelector,
        Func<TSource, DbColumnName> columnSelector
    )
    {
        return
        [
            .. sourcesInEmissionOrder
                .GroupBy(source => tableModelSelector(source).Table)
                .Select(group => new ProjectionSourceTableGroup(
                    tableModelSelector(group.First()),
                    [.. group.Select(columnSelector)]
                )),
        ];
    }

    /// <summary>
    /// Appends a single source branch.
    /// </summary>
    /// <param name="projectedColumn">Name the branch projects its ids under.</param>
    /// <param name="emitDistinct">
    /// Whether the branch deduplicates on its own. Set only when the branch is the sole source,
    /// because a multi-branch projection deduplicates through the enclosing <c>UNION</c> instead.
    /// </param>
    public static void Append(
        SqlWriter writer,
        IPlanSqlDialect planDialect,
        ProjectionSourceTableGroup sourceGroup,
        PlanSqlSourceAliases aliases,
        DbColumnName projectedColumn,
        ProjectionSourceFilter sourceFilter,
        bool emitDistinct,
        string planDescription
    )
    {
        var rootScopeLocatorColumn =
            RelationalResourceModelCompileValidator.ResolveRootScopeLocatorColumnOrThrow(
                sourceGroup.TableModel,
                planDescription
            );
        var isExpanded = sourceGroup.ColumnsInOrder.Count > 1;

        writer.Append("SELECT ");

        if (emitDistinct)
        {
            writer.Append("DISTINCT ");
        }

        if (isExpanded)
        {
            AppendQualifiedColumn(writer, aliases.RowSetAlias, projectedColumn);
            writer.AppendLine();
        }
        else
        {
            AppendQualifiedColumn(writer, aliases.TableAlias, sourceGroup.ColumnsInOrder[0]);
            writer.Append(" AS ").AppendQuoted(projectedColumn.Value).AppendLine();
        }

        writer.Append("FROM ").AppendTable(sourceGroup.TableModel.Table).AppendLine($" {aliases.TableAlias}");

        ProjectionSourceFilterSql.AppendSourceJoin(
            writer,
            aliases.TableAlias,
            rootScopeLocatorColumn,
            sourceFilter
        );

        if (isExpanded)
        {
            AppendCorrelatedRowSet(writer, planDialect, sourceGroup, aliases, projectedColumn);
        }

        ProjectionSourceFilterSql.AppendSourceWhere(
            writer,
            aliases.TableAlias,
            rootScopeLocatorColumn,
            isExpanded ? aliases.RowSetAlias : aliases.TableAlias,
            isExpanded ? projectedColumn : sourceGroup.ColumnsInOrder[0],
            sourceFilter
        );
    }

    private static void AppendCorrelatedRowSet(
        SqlWriter writer,
        IPlanSqlDialect planDialect,
        ProjectionSourceTableGroup sourceGroup,
        PlanSqlSourceAliases aliases,
        DbColumnName projectedColumn
    )
    {
        writer.Append(planDialect.CorrelatedRowSetJoinKeyword).Append(" (VALUES ");

        for (var index = 0; index < sourceGroup.ColumnsInOrder.Count; index++)
        {
            if (index > 0)
            {
                writer.Append(", ");
            }

            writer.Append("(");
            AppendQualifiedColumn(writer, aliases.TableAlias, sourceGroup.ColumnsInOrder[index]);
            writer.Append(")");
        }

        writer.Append($") AS {aliases.RowSetAlias}(").AppendQuoted(projectedColumn.Value).AppendLine(")");
    }

    private static void AppendQualifiedColumn(SqlWriter writer, string tableAlias, DbColumnName columnName)
    {
        writer.Append($"{tableAlias}.").AppendQuoted(columnName.Value);
    }
}
