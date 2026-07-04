// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Assembles a multi-statement SQL batch from a <see cref="ResourceReadPlan"/>
/// and <see cref="PageKeysetSpec"/> for single-command multi-result hydration.
/// </summary>
/// <remarks>
/// The keyset batch emits result sets in a deterministic sequence:
/// <list type="number">
/// <item>Optional <c>TotalCount</c> (single row, single column)</item>
/// <item><c>dms.Document</c> metadata joined to the page keyset</item>
/// <item>Root table rows (from <c>TablePlansInDependencyOrder[0]</c>)</item>
/// <item>Child table rows (from <c>TablePlansInDependencyOrder[1..n]</c>)</item>
/// <item>Descriptor URI rows (from <c>DescriptorProjectionPlansInOrder[0..n]</c>)</item>
/// <item>
/// Optional document-reference auxiliary lookup rows (when
/// <see cref="ResourceReadPlan.DocumentReferenceLookup"/> is non-null)
/// </item>
/// </list>
/// When the PostgreSQL single-document fast path is enabled for <see cref="PageKeysetSpec.Single"/>,
/// the batch skips keyset materialization and starts with document metadata, followed by the same
/// table, descriptor, and document-reference result-set sequence.
/// </remarks>
public static class HydrationBatchBuilder
{
    /// <summary>
    /// Builds the complete SQL batch command text.
    /// </summary>
    /// <param name="plan">The compiled resource read plan.</param>
    /// <param name="keyset">The page keyset specification.</param>
    /// <param name="dialect">The SQL dialect.</param>
    /// <returns>The assembled SQL command text.</returns>
    public static string Build(ResourceReadPlan plan, PageKeysetSpec keyset, SqlDialect dialect) =>
        Build(plan, keyset, dialect, new HydrationExecutionOptions(IncludeDescriptorProjection: true));

    /// <summary>
    /// Builds the complete SQL batch command text.
    /// </summary>
    /// <param name="plan">The compiled resource read plan.</param>
    /// <param name="keyset">The page keyset specification.</param>
    /// <param name="dialect">The SQL dialect.</param>
    /// <param name="executionOptions">Controls optional projection work in the batch.</param>
    /// <returns>The assembled SQL command text.</returns>
    public static string Build(
        ResourceReadPlan plan,
        PageKeysetSpec keyset,
        SqlDialect dialect,
        HydrationExecutionOptions executionOptions
    )
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(keyset);

        var sqlDialect = SqlDialectFactory.Create(dialect);
        var planDialect = PlanSqlDialectFactory.Create(dialect);
        var writer = new SqlWriter(sqlDialect);

        if (ShouldUseSingleDocumentBatch(keyset, dialect, executionOptions))
        {
            return BuildSingleDocumentBatch(plan, planDialect, writer, executionOptions);
        }

        return BuildExistingKeysetBatch(plan, keyset, planDialect, writer, executionOptions);
    }

    private static bool ShouldUseSingleDocumentBatch(
        PageKeysetSpec keyset,
        SqlDialect dialect,
        HydrationExecutionOptions executionOptions
    ) =>
        dialect is SqlDialect.Pgsql
        && keyset is PageKeysetSpec.Single
        && executionOptions.UseSingleDocumentFastPath;

    private static string BuildExistingKeysetBatch(
        ResourceReadPlan plan,
        PageKeysetSpec keyset,
        IPlanSqlDialect planDialect,
        SqlWriter writer,
        HydrationExecutionOptions executionOptions
    )
    {
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

        // 6. Descriptor projection selects in deterministic plan order
        if (executionOptions.IncludeDescriptorProjection)
        {
            foreach (var descriptorPlan in plan.DescriptorProjectionPlansInOrder)
            {
                writer.AppendLine(EnsureTrailingSemicolon(descriptorPlan.SelectByKeysetSql));
                writer.AppendLine();
            }
        }

        // 7. Document-reference auxiliary lookup (gated by plan property AND the caller-supplied
        //    execution option — write-path callers that discard the lookup result opt out).
        if (
            executionOptions.IncludeDocumentReferenceLookup
            && plan.DocumentReferenceLookup is { } documentReferenceLookup
        )
        {
            writer.AppendLine(EnsureTrailingSemicolon(documentReferenceLookup.SelectByKeysetSql));
            writer.AppendLine();
        }

        return writer.ToString();
    }

    private static string BuildSingleDocumentBatch(
        ResourceReadPlan plan,
        IPlanSqlDialect planDialect,
        SqlWriter writer,
        HydrationExecutionOptions executionOptions
    )
    {
        // 1. Document metadata select
        planDialect.AppendSingleDocumentMetadataSelect(
            writer,
            HydrationSqlConventions.SingleDocumentIdParameterName
        );
        writer.AppendLine();

        // 2. Table hydration selects in dependency order
        for (
            var tablePlanIndex = 0;
            tablePlanIndex < plan.TablePlansInDependencyOrder.Length;
            tablePlanIndex++
        )
        {
            var tablePlan = plan.TablePlansInDependencyOrder[tablePlanIndex];
            writer.AppendLine(
                EnsureTrailingSemicolon(
                    RequireSingleDocumentSql(
                        $"table read plan at index '{tablePlanIndex}' for table '{tablePlan.TableModel.Table}'",
                        tablePlan.SelectBySingleDocumentSql
                    )
                )
            );
            writer.AppendLine();
        }

        // 3. Descriptor projection selects in deterministic plan order
        if (executionOptions.IncludeDescriptorProjection)
        {
            for (
                var descriptorPlanIndex = 0;
                descriptorPlanIndex < plan.DescriptorProjectionPlansInOrder.Length;
                descriptorPlanIndex++
            )
            {
                writer.AppendLine(
                    EnsureTrailingSemicolon(
                        RequireSingleDocumentSql(
                            $"descriptor projection plan at index '{descriptorPlanIndex}'",
                            plan.DescriptorProjectionPlansInOrder[
                                descriptorPlanIndex
                            ].SelectBySingleDocumentSql
                        )
                    )
                );
                writer.AppendLine();
            }
        }

        // 4. Document-reference auxiliary lookup (gated by plan property AND the caller-supplied
        //    execution option — write-path callers that discard the lookup result opt out).
        if (
            executionOptions.IncludeDocumentReferenceLookup
            && plan.DocumentReferenceLookup is { } documentReferenceLookup
        )
        {
            writer.AppendLine(
                EnsureTrailingSemicolon(
                    RequireSingleDocumentSql(
                        "document-reference lookup plan",
                        documentReferenceLookup.SelectBySingleDocumentSql
                    )
                )
            );
            writer.AppendLine();
        }

        return writer.ToString();
    }

    private static string RequireSingleDocumentSql(string planDescription, string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new InvalidOperationException(
                "PostgreSQL single-document hydration requires "
                    + $"{planDescription} to provide SelectBySingleDocumentSql."
            );
        }

        return sql;
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
                AddScalarParameter(
                    command,
                    HydrationSqlConventions.SingleDocumentIdParameterName,
                    single.DocumentId
                );
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
                    .AppendParameter(HydrationSqlConventions.SingleDocumentIdParameterName)
                    .AppendLine(");");
                break;

            case PageKeysetSpec.Query query when HasZeroLimit(query):
                AppendEmptyKeysetMaterialization(writer, keyset, quotedDocIdCol);
                break;

            case PageKeysetSpec.Query query:
                AppendQueryKeysetMaterialization(writer, keyset, query, quotedDocIdCol);
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(spec),
                    spec,
                    "Unexpected PageKeysetSpec variant."
                );
        }
    }

    private static void AppendQueryKeysetMaterialization(
        SqlWriter writer,
        KeysetTableContract keyset,
        PageKeysetSpec.Query query,
        string quotedDocIdCol
    )
    {
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
    }

    private static void AppendEmptyKeysetMaterialization(
        SqlWriter writer,
        KeysetTableContract keyset,
        string quotedDocIdCol
    )
    {
        writer
            .Append("INSERT INTO ")
            .AppendRelation(keyset.Table)
            .Append(" (")
            .Append(quotedDocIdCol)
            .AppendLine(")")
            .Append("SELECT CAST(NULL AS bigint) AS ")
            .Append(quotedDocIdCol)
            .AppendLine(" WHERE 1 = 0;");
    }

    private static bool HasZeroLimit(PageKeysetSpec.Query query)
    {
        foreach (var parameter in query.Plan.PageParametersInOrder)
        {
            if (parameter.Role is not QuerySqlParameterRole.Limit)
            {
                continue;
            }

            return query.ParameterValues.TryGetValue(parameter.ParameterName, out var limitValue)
                && IsZeroLimitValue(limitValue);
        }

        return false;
    }

    private static bool IsZeroLimitValue(object? value)
    {
        return value switch
        {
            byte typedValue => typedValue == 0,
            sbyte typedValue => typedValue == 0,
            short typedValue => typedValue == 0,
            ushort typedValue => typedValue == 0,
            int typedValue => typedValue == 0,
            uint typedValue => typedValue == 0,
            long typedValue => typedValue == 0,
            ulong typedValue => typedValue == 0,
            _ => false,
        };
    }

    private static void AddQueryParameters(DbCommand command, PageKeysetSpec.Query query)
    {
        var requiredParameters = GetRequiredParameters(query.Plan);
        ValidateRequiredParameterValues(
            query.ParameterValues,
            [.. requiredParameters.Select(static parameter => parameter.ParameterName)]
        );

        foreach (var parameter in requiredParameters)
        {
            AddParameter(command, parameter, query.ParameterValues[parameter.ParameterName]);
        }
    }

    private static QuerySqlParameter[] GetRequiredParameters(PageDocumentIdSqlPlan plan)
    {
        List<QuerySqlParameter> requiredParameters = [];

        AddRequiredParameters(requiredParameters, plan.PageParametersInOrder);

        if (plan.TotalCountParametersInOrder is { } totalCountParameters)
        {
            AddRequiredParameters(requiredParameters, totalCountParameters);
        }

        return [.. requiredParameters];
    }

    private static void AddRequiredParameters(
        List<QuerySqlParameter> requiredParameters,
        IReadOnlyList<QuerySqlParameter> parameters
    )
    {
        foreach (var parameter in parameters)
        {
            if (
                TryGetRequiredParameter(
                    requiredParameters,
                    parameter.ParameterName,
                    out var existingParameter
                )
            )
            {
                if (existingParameter != parameter)
                {
                    throw new InvalidOperationException(
                        "Hydration query keyset cannot bind parameter "
                            + $"'{parameter.ParameterName}' with conflicting binding metadata."
                    );
                }

                continue;
            }

            requiredParameters.Add(parameter);
        }
    }

    private static bool TryGetRequiredParameter(
        IReadOnlyList<QuerySqlParameter> requiredParameters,
        string candidateParameterName,
        [NotNullWhen(true)] out QuerySqlParameter? parameter
    )
    {
        parameter = requiredParameters.FirstOrDefault(candidateParameter =>
            string.Equals(
                candidateParameter.ParameterName,
                candidateParameterName,
                StringComparison.OrdinalIgnoreCase
            )
        );

        return parameter is not null;
    }

    private static void ValidateRequiredParameterValues(
        IReadOnlyDictionary<string, object?> parameterValues,
        IReadOnlyList<string> requiredParameterNames
    )
    {
        List<string> missingParameterNames = [];

        foreach (var parameterName in requiredParameterNames)
        {
            if (!parameterValues.ContainsKey(parameterName))
            {
                missingParameterNames.Add(parameterName);
            }
        }

        if (missingParameterNames.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            "Hydration query keyset is missing required parameter values for "
                + $"[{string.Join(", ", missingParameterNames.ConvertAll(parameterName => $"'{parameterName}'"))}]."
        );
    }

    private static void AddParameter(DbCommand command, QuerySqlParameter parameter, object? value)
    {
        switch (parameter.Binding.Kind)
        {
            case QuerySqlParameterBindingKind.Scalar:
                AddScalarParameter(command, parameter.ParameterName, value);
                return;

            case QuerySqlParameterBindingKind.PgsqlArray:
                AddPgsqlArrayParameter(command, parameter.ParameterName, value);
                return;

            case QuerySqlParameterBindingKind.MssqlStructured:
                AddMssqlStructuredParameter(command, parameter, value);
                return;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(parameter),
                    parameter.Binding.Kind,
                    "Unsupported query-parameter binding kind."
                );
        }
    }

    private static void AddScalarParameter(DbCommand command, string bareName, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = $"@{bareName}";
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static void AddPgsqlArrayParameter(DbCommand command, string bareName, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = $"@{bareName}";
        // PostgreSQL array parameters carry either claim EdOrg ids (long[]) or namespace prefix LIKE
        // patterns (string[]); Npgsql infers the element type from the runtime array.
        parameter.Value = value switch
        {
            IReadOnlyList<long> int64Values => int64Values.ToArray(),
            IReadOnlyList<string> stringValues => stringValues.ToArray(),
            _ => throw new InvalidOperationException(
                "Hydration query keyset parameter "
                    + $"'{bareName}' requires an IReadOnlyList<long> or IReadOnlyList<string> runtime value."
            ),
        };
        command.Parameters.Add(parameter);
    }

    private static void AddMssqlStructuredParameter(
        DbCommand command,
        QuerySqlParameter querySqlParameter,
        object? value
    )
    {
        var dbParameter = command.CreateParameter();
        dbParameter.ParameterName = $"@{querySqlParameter.ParameterName}";
        dbParameter.Value = CreateStructuredInt64Table(
            querySqlParameter.Binding.StructuredColumnName
                ?? throw new InvalidOperationException(
                    $"Structured binding for parameter '{querySqlParameter.ParameterName}' is missing a column name."
                ),
            RequireInt64List(value, querySqlParameter.ParameterName)
        );

        if (dbParameter is not SqlParameter sqlParameter)
        {
            throw new InvalidOperationException(
                "SQL Server structured query-parameter binding requires a SqlParameter instance."
            );
        }

        sqlParameter.SqlDbType = SqlDbType.Structured;
        sqlParameter.TypeName =
            querySqlParameter.Binding.StructuredTypeName
            ?? throw new InvalidOperationException(
                $"Structured binding for parameter '{querySqlParameter.ParameterName}' is missing a type name."
            );

        command.Parameters.Add(sqlParameter);
    }

    private static IReadOnlyList<long> RequireInt64List(object? value, string parameterName)
    {
        if (value is IReadOnlyList<long> int64Values)
        {
            return int64Values;
        }

        throw new InvalidOperationException(
            "Hydration query keyset parameter "
                + $"'{parameterName}' requires an IReadOnlyList<long> runtime value."
        );
    }

    private static DataTable CreateStructuredInt64Table(
        string structuredColumnName,
        IReadOnlyList<long> int64Values
    )
    {
        DataTable structuredTable = new();
        structuredTable.Columns.Add(structuredColumnName, typeof(long));

        foreach (var value in int64Values)
        {
            structuredTable.Rows.Add(value);
        }

        return structuredTable;
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
