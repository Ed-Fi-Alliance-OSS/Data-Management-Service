// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

internal static class ProjectionSourceFilterSql
{
    public static void Append(
        SqlWriter writer,
        DbTableModel tableModel,
        string tableAlias,
        DbColumnName nonNullColumn,
        ProjectionSourceFilter sourceFilter,
        string planDescription
    )
    {
        var rootScopeLocatorColumn =
            RelationalResourceModelCompileValidator.ResolveRootScopeLocatorColumnOrThrow(
                tableModel,
                planDescription
            );

        Append(writer, tableAlias, rootScopeLocatorColumn, nonNullColumn, sourceFilter);
    }

    private static void Append(
        SqlWriter writer,
        string tableAlias,
        DbColumnName rootScopeLocatorColumn,
        DbColumnName nonNullColumn,
        ProjectionSourceFilter sourceFilter
    )
    {
        if (sourceFilter.KeysetTable is not null)
        {
            var keysetAlias = PlanNamingConventions.GetFixedAlias(PlanSqlAliasRole.Keyset);

            writer
                .Append("INNER JOIN ")
                .AppendRelation(sourceFilter.KeysetTable.Table)
                .Append($" {keysetAlias} ON ");
            AppendQualifiedColumn(writer, tableAlias, rootScopeLocatorColumn);
            writer.Append(" = ");
            AppendQualifiedColumn(writer, keysetAlias, sourceFilter.KeysetTable.DocumentIdColumnName);
            writer.AppendLine();
            writer.Append("WHERE ");
            AppendQualifiedColumn(writer, tableAlias, nonNullColumn);
            writer.AppendLine(" IS NOT NULL");

            return;
        }

        writer.Append("WHERE ");
        AppendQualifiedColumn(writer, tableAlias, rootScopeLocatorColumn);
        writer.Append(" = ");
        writer.AppendParameter(HydrationSqlConventions.SingleDocumentIdParameterName);
        writer.AppendLine();
        writer.Append("AND ");
        AppendQualifiedColumn(writer, tableAlias, nonNullColumn);
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
