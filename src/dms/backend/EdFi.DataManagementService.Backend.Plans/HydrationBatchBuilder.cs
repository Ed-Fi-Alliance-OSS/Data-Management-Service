// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Assembles a multi-statement SQL batch from a <see cref="ResourceReadPlan"/>
/// and <see cref="PageKeysetSpec"/> for single-command multi-result hydration.
/// </summary>
/// <remarks>
/// The batch emits result sets in a deterministic sequence:
/// <list type="number">
/// <item>Optional <c>TotalCount</c> (single row, single column)</item>
/// <item><c>dms.Document</c> metadata joined to the page keyset</item>
/// <item>Root table rows (from <c>TablePlansInDependencyOrder[0]</c>)</item>
/// <item>Child table rows (from <c>TablePlansInDependencyOrder[1..n]</c>)</item>
/// </list>
/// </remarks>
public static class HydrationBatchBuilder
{
    internal const string DocumentIdParameterName = "DocumentId";

    /// <summary>
    /// Builds the complete SQL batch command text.
    /// </summary>
    /// <param name="plan">The compiled resource read plan.</param>
    /// <param name="keyset">The page keyset specification.</param>
    /// <param name="dialect">The SQL dialect.</param>
    /// <returns>The assembled SQL command text.</returns>
    public static string Build(ResourceReadPlan plan, PageKeysetSpec keyset, SqlDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(keyset);

        var sqlDialect = SqlDialectFactory.Create(dialect);
        var planDialect = PlanSqlDialectFactory.Create(dialect);
        var writer = new SqlWriter(sqlDialect);

        // 1. Create keyset temp table
        planDialect.AppendCreateKeysetTempTable(writer, plan.KeysetTable);
        writer.AppendLine();

        // 2. Materialize keyset
        AppendKeysetMaterialization(writer, plan.KeysetTable, keyset);
        writer.AppendLine();

        // 3. Optional total count
        if (keyset is PageKeysetSpec.Query { Plan.TotalCountSql: not null } queryWithCount)
        {
            writer.AppendLine(EnsureTrailingSemicolon(queryWithCount.Plan.TotalCountSql));
            writer.AppendLine();
        }

        // 4. Document metadata select
        planDialect.AppendDocumentMetadataSelect(writer, plan.KeysetTable);
        writer.AppendLine();

        // 5. Table hydration selects in dependency order
        foreach (var tablePlan in plan.TablePlansInDependencyOrder)
        {
            writer.AppendLine(tablePlan.SelectByKeysetSql);
            writer.AppendLine();
        }

        return writer.ToString();
    }

    /// <summary>
    /// Adds parameters to a <see cref="DbCommand"/> based on the keyset specification.
    /// </summary>
    /// <param name="command">The database command to add parameters to.</param>
    /// <param name="keyset">The page keyset specification.</param>
    public static void AddParameters(DbCommand command, PageKeysetSpec keyset)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(keyset);

        switch (keyset)
        {
            case PageKeysetSpec.Single single:
                AddParameter(command, DocumentIdParameterName, single.DocumentId);
                break;

            case PageKeysetSpec.Query query:
                AddQueryParameters(command, query);
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(keyset),
                    keyset,
                    "Unexpected PageKeysetSpec variant."
                );
        }
    }

    private static void AppendKeysetMaterialization(
        SqlWriter writer,
        KeysetTableContract keyset,
        PageKeysetSpec spec
    )
    {
        var quotedDocIdCol = writer.Dialect.QuoteIdentifier(keyset.DocumentIdColumnName.Value);

        switch (spec)
        {
            case PageKeysetSpec.Single:
                writer
                    .Append("INSERT INTO ")
                    .AppendRelation(keyset.Table)
                    .Append(" (")
                    .Append(quotedDocIdCol)
                    .Append(") VALUES (")
                    .AppendParameter(DocumentIdParameterName)
                    .AppendLine(");");
                break;

            case PageKeysetSpec.Query query:
                writer
                    .AppendLine("WITH page_ids AS (")
                    .AppendLine(StripTrailingSemicolon(query.Plan.PageDocumentIdSql))
                    .AppendLine(")")
                    .Append("INSERT INTO ")
                    .AppendRelation(keyset.Table)
                    .Append(" (")
                    .Append(quotedDocIdCol)
                    .AppendLine(")")
                    .Append("SELECT ")
                    .Append(quotedDocIdCol)
                    .AppendLine(" FROM page_ids;");
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(spec),
                    spec,
                    "Unexpected PageKeysetSpec variant."
                );
        }
    }

    private static void AddQueryParameters(DbCommand command, PageKeysetSpec.Query query)
    {
        foreach (var param in query.Plan.PageParametersInOrder)
        {
            if (query.ParameterValues.TryGetValue(param.ParameterName, out var value))
            {
                AddParameter(command, param.ParameterName, value);
            }
        }

        if (query.Plan.TotalCountParametersInOrder is { } totalCountParams)
        {
            foreach (var param in totalCountParams)
            {
                // Skip if already added from page parameters (same name, same value)
                if (command.Parameters.Contains($"@{param.ParameterName}"))
                {
                    continue;
                }

                if (query.ParameterValues.TryGetValue(param.ParameterName, out var value))
                {
                    AddParameter(command, param.ParameterName, value);
                }
            }
        }
    }

    private static void AddParameter(DbCommand command, string bareName, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = $"@{bareName}";
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    /// <summary>
    /// Ensures a SQL statement ends with a semicolon so it is properly terminated
    /// when embedded in a multi-statement batch.
    /// </summary>
    private static string EnsureTrailingSemicolon(string sql)
    {
        var trimmed = sql.AsSpan().TrimEnd();
        return trimmed.Length > 0 && trimmed[^1] == ';' ? sql : $"{trimmed};";
    }

    /// <summary>
    /// Strips a trailing semicolon (and surrounding whitespace) from compiled SQL so it can
    /// be safely embedded inside a CTE body. Compiled plan SQL (e.g. from
    /// <see cref="PageDocumentIdSqlCompiler"/>) includes a trailing semicolon as a statement
    /// terminator, which is invalid inside <c>WITH ... AS (...)</c>.
    /// </summary>
    private static string StripTrailingSemicolon(string sql)
    {
        var trimmed = sql.AsSpan().TrimEnd();
        if (trimmed.Length > 0 && trimmed[^1] == ';')
        {
            trimmed = trimmed[..^1].TrimEnd();
        }

        return trimmed.ToString();
    }
}
