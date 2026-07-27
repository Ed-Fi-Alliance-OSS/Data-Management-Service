// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Emits the page-scoping clauses shared by projection source branches. Scoping is split into a
/// join phase and a where phase so a caller can place additional FROM-clause sources between them.
/// </summary>
internal static class ProjectionSourceFilterSql
{
    /// <summary>
    /// Appends the FROM-clause join that scopes a projection source to the page keyset. Emits
    /// nothing for the single-document filter, which scopes through a WHERE predicate instead.
    /// </summary>
    public static void AppendSourceJoin(
        SqlWriter writer,
        string tableAlias,
        DbColumnName rootScopeLocatorColumn,
        ProjectionSourceFilter sourceFilter
    )
    {
        if (sourceFilter.KeysetTable is null)
        {
            return;
        }

        var keysetAlias = PlanNamingConventions.GetFixedAlias(PlanSqlAliasRole.Keyset);

        writer
            .Append("INNER JOIN ")
            .AppendRelation(sourceFilter.KeysetTable.Table)
            .Append($" {keysetAlias} ON ");
        AppendQualifiedColumn(writer, tableAlias, rootScopeLocatorColumn);
        writer.Append(" = ");
        AppendQualifiedColumn(writer, keysetAlias, sourceFilter.KeysetTable.DocumentIdColumnName);
        writer.AppendLine();
    }

    /// <summary>
    /// Appends the WHERE clause for a projection source: the single-document root-scope predicate
    /// when the filter is not keyset-based, followed by the non-null predicate on the projected
    /// value.
    /// </summary>
    /// <param name="nonNullAlias">
    /// Alias owning the projected value. This is the source table for a single-column branch and
    /// the correlated row-set alias for a branch that expands several columns.
    /// </param>
    public static void AppendSourceWhere(
        SqlWriter writer,
        string tableAlias,
        DbColumnName rootScopeLocatorColumn,
        string nonNullAlias,
        DbColumnName nonNullColumn,
        ProjectionSourceFilter sourceFilter
    )
    {
        writer.Append("WHERE ");

        if (sourceFilter.KeysetTable is null)
        {
            AppendQualifiedColumn(writer, tableAlias, rootScopeLocatorColumn);
            writer.Append(" = ");
            writer.AppendParameter(HydrationSqlConventions.SingleDocumentIdParameterName);
            writer.AppendLine();
            writer.Append("AND ");
        }

        AppendQualifiedColumn(writer, nonNullAlias, nonNullColumn);
        writer.AppendLine(" IS NOT NULL");
    }

    private static void AppendQualifiedColumn(SqlWriter writer, string tableAlias, DbColumnName columnName)
    {
        writer.Append($"{tableAlias}.").AppendQuoted(columnName.Value);
    }
}

internal sealed record ProjectionSourceFilter(KeysetTableContract? KeysetTable)
{
    public static ProjectionSourceFilter SingleDocument { get; } = new(KeysetTable: null);

    public static ProjectionSourceFilter Keyset(KeysetTableContract keysetTable)
    {
        ArgumentNullException.ThrowIfNull(keysetTable);

        return new ProjectionSourceFilter(keysetTable);
    }
}
