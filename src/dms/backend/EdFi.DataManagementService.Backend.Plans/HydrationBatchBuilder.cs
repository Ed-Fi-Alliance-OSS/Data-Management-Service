// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
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
/// When the dialect supports single-document hydration and that fast path is enabled for
/// <see cref="PageKeysetSpec.Single"/>, the batch skips keyset materialization and starts with
/// document metadata, followed by the same table, descriptor, and document-reference result-set sequence.
/// </remarks>
public static class HydrationBatchBuilder
{
    private static readonly ConditionalWeakTable<
        ResourceReadPlan,
        ConcurrentDictionary<SingleDocumentBatchCacheKey, Lazy<string>>
    > _singleDocumentBatchCache = new();

    private readonly record struct SingleDocumentBatchCacheKey(
        SqlDialect Dialect,
        bool IncludeDescriptorProjection,
        bool IncludeDocumentReferenceLookup
    )
    {
        public static SingleDocumentBatchCacheKey From(
            IPlanSqlDialect planDialect,
            HydrationExecutionOptions executionOptions
        ) =>
            new(
                planDialect.Dialect,
                executionOptions.IncludeDescriptorProjection,
                executionOptions.IncludeDocumentReferenceLookup
            );
    }

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

        if (ShouldUseSingleDocumentBatch(keyset, planDialect, executionOptions))
        {
            return GetOrBuildSingleDocumentBatch(plan, planDialect, sqlDialect, executionOptions);
        }

        var writer = new SqlWriter(sqlDialect);

        return BuildExistingKeysetBatch(plan, keyset, planDialect, writer, executionOptions);
    }

    /// <summary>
    /// Builds a metadata-only batch for an already-planned query page. The batch materializes the
    /// query keyset and returns only optional total count plus <c>dms.Document</c> metadata for the
    /// selected candidates.
    /// </summary>
    public static string BuildCandidateMetadataBatch(
        ResourceReadPlan plan,
        PageKeysetSpec.Query keyset,
        SqlDialect dialect
    )
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(keyset);

        var sqlDialect = SqlDialectFactory.Create(dialect);
        var planDialect = PlanSqlDialectFactory.Create(dialect);
        var writer = new SqlWriter(sqlDialect);

        planDialect.AppendCreateKeysetTempTable(writer, plan.KeysetTable);
        writer.AppendLine();

        AppendKeysetMaterialization(writer, plan.KeysetTable, keyset);
        writer.AppendLine();

        if (keyset.Plan.TotalCountSql is not null)
        {
            writer.AppendLine(EnsureTrailingSemicolon(keyset.Plan.TotalCountSql));
            writer.AppendLine();
        }

        planDialect.AppendDocumentMetadataSelect(writer, plan.KeysetTable);

        return writer.ToString();
    }

    /// <summary>
    /// Builds the single-document hydration batch for a document id that is not client-known at build
    /// time. The id is still referenced through the single-document parameter token — callers
    /// co-batching this batch substitute that token with their captured-target expression — but a
    /// keyset materialization is guarded so an absent id materializes no keyset row rather than
    /// inserting NULL into it.
    /// </summary>
    /// <param name="plan">The compiled resource read plan.</param>
    /// <param name="dialect">The SQL dialect.</param>
    /// <param name="executionOptions">Controls optional projection work in the batch.</param>
    /// <param name="keysetRowGuardPredicateSql">
    /// Raw predicate that is true only when the captured id exists, for example the composite
    /// carrier's captured-target-present predicate.
    /// </param>
    /// <returns>The assembled SQL command text.</returns>
    public static string BuildGuardedSingleDocumentBatch(
        ResourceReadPlan plan,
        SqlDialect dialect,
        HydrationExecutionOptions executionOptions,
        string keysetRowGuardPredicateSql
    )
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(keysetRowGuardPredicateSql);

        var sqlDialect = SqlDialectFactory.Create(dialect);
        var planDialect = PlanSqlDialectFactory.Create(dialect);
        var keyset = new PageKeysetSpec.Single(0);

        if (ShouldUseSingleDocumentBatch(keyset, planDialect, executionOptions))
        {
            // The fast path is pure selects, so an absent id yields zero-row result sets on its own.
            return GetOrBuildSingleDocumentBatch(plan, planDialect, sqlDialect, executionOptions);
        }

        var writer = new SqlWriter(sqlDialect);

        return BuildExistingKeysetBatch(
            plan,
            keyset,
            planDialect,
            writer,
            executionOptions,
            keysetRowGuardPredicateSql
        );
    }

    private static bool ShouldUseSingleDocumentBatch(
        PageKeysetSpec keyset,
        IPlanSqlDialect planDialect,
        HydrationExecutionOptions executionOptions
    ) =>
        planDialect.SupportsSingleDocumentHydration
        && keyset is PageKeysetSpec.Single
        && executionOptions.UseSingleDocumentFastPath;

    private static string GetOrBuildSingleDocumentBatch(
        ResourceReadPlan plan,
        IPlanSqlDialect planDialect,
        ISqlDialect sqlDialect,
        HydrationExecutionOptions executionOptions
    )
    {
        var cacheKey = SingleDocumentBatchCacheKey.From(planDialect, executionOptions);
        var cacheForPlan = _singleDocumentBatchCache.GetValue(
            plan,
            static _ => new ConcurrentDictionary<SingleDocumentBatchCacheKey, Lazy<string>>()
        );
        var cachedBatch = cacheForPlan.GetOrAdd(
            cacheKey,
            static (key, state) =>
                new Lazy<string>(
                    () =>
                        BuildSingleDocumentBatch(
                            state.Plan,
                            state.PlanDialect,
                            new SqlWriter(state.SqlDialect),
                            key
                        ),
                    LazyThreadSafetyMode.ExecutionAndPublication
                ),
            (Plan: plan, PlanDialect: planDialect, SqlDialect: sqlDialect)
        );

        try
        {
            return cachedBatch.Value;
        }
        catch
        {
            cacheForPlan.TryRemove(cacheKey, out _);
            throw;
        }
    }

    private static string BuildExistingKeysetBatch(
        ResourceReadPlan plan,
        PageKeysetSpec keyset,
        IPlanSqlDialect planDialect,
        SqlWriter writer,
        HydrationExecutionOptions executionOptions,
        string? singleRowGuardPredicateSql = null
    )
    {
        // 1. Create keyset temp table
        planDialect.AppendCreateKeysetTempTable(writer, plan.KeysetTable);
        writer.AppendLine();

        // 2. Materialize keyset
        AppendKeysetMaterialization(writer, plan.KeysetTable, keyset, singleRowGuardPredicateSql);
        writer.AppendLine();

        // 3. Optional total count
        if (keyset is PageKeysetSpec.Query { Plan.TotalCountSql: not null } queryWithCount)
        {
            writer.AppendLine(PlanSqlStatementText.AsTerminatedStatement(queryWithCount.Plan.TotalCountSql));
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
                writer.AppendLine(
                    PlanSqlStatementText.AsTerminatedStatement(descriptorPlan.SelectByKeysetSql)
                );
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
            writer.AppendLine(
                PlanSqlStatementText.AsTerminatedStatement(documentReferenceLookup.SelectByKeysetSql)
            );
            writer.AppendLine();
        }

        return writer.ToString();
    }

    private static string BuildSingleDocumentBatch(
        ResourceReadPlan plan,
        IPlanSqlDialect planDialect,
        SqlWriter writer,
        SingleDocumentBatchCacheKey cacheKey
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
                PlanSqlStatementText.AsTerminatedStatement(
                    RequireSingleDocumentSql(
                        planDialect,
                        $"table read plan at index '{tablePlanIndex}' for table '{tablePlan.TableModel.Table}'",
                        tablePlan.SelectBySingleDocumentSql
                    )
                )
            );
            writer.AppendLine();
        }

        // 3. Descriptor projection selects in deterministic plan order
        if (cacheKey.IncludeDescriptorProjection)
        {
            for (
                var descriptorPlanIndex = 0;
                descriptorPlanIndex < plan.DescriptorProjectionPlansInOrder.Length;
                descriptorPlanIndex++
            )
            {
                writer.AppendLine(
                    PlanSqlStatementText.AsTerminatedStatement(
                        RequireSingleDocumentSql(
                            planDialect,
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
            cacheKey.IncludeDocumentReferenceLookup
            && plan.DocumentReferenceLookup is { } documentReferenceLookup
        )
        {
            writer.AppendLine(
                PlanSqlStatementText.AsTerminatedStatement(
                    RequireSingleDocumentSql(
                        planDialect,
                        "document-reference lookup plan",
                        documentReferenceLookup.SelectBySingleDocumentSql
                    )
                )
            );
            writer.AppendLine();
        }

        return writer.ToString();
    }

    private static string RequireSingleDocumentSql(
        IPlanSqlDialect planDialect,
        string planDescription,
        string? sql
    )
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new InvalidOperationException(
                $"{planDialect.DisplayName} single-document hydration requires "
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

            case PageKeysetSpec.SelectedPage selectedPage:
                AddSelectedPageParameters(command, selectedPage);
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
        PageKeysetSpec spec,
        string? singleRowGuardPredicateSql = null
    )
    {
        var quotedDocIdCol = writer.Dialect.QuoteIdentifier(keyset.DocumentIdColumnName.Value);

        switch (spec)
        {
            case PageKeysetSpec.Single when singleRowGuardPredicateSql is not null:
                // Same INSERT ... SELECT ... WHERE shape as the empty-keyset materialization, so an
                // absent captured id materializes no row instead of inserting NULL.
                writer
                    .Append("INSERT INTO ")
                    .AppendRelation(keyset.Table)
                    .Append(" (")
                    .Append(quotedDocIdCol)
                    .AppendLine(")")
                    .Append("SELECT ")
                    .AppendParameter(HydrationSqlConventions.SingleDocumentIdParameterName)
                    .Append(" WHERE ")
                    .Append(singleRowGuardPredicateSql)
                    .AppendLine(";");
                break;

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

            case PageKeysetSpec.SelectedPage { DocumentIds.Count: 0 }:
                AppendEmptyKeysetMaterialization(writer, keyset, quotedDocIdCol);
                break;

            case PageKeysetSpec.SelectedPage selectedPage:
                AppendSelectedPageKeysetMaterialization(writer, keyset, selectedPage, quotedDocIdCol);
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
            .AppendLine(PlanSqlStatementText.AsEmbeddableBody(query.Plan.PageDocumentIdSql))
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

    private static void AppendSelectedPageKeysetMaterialization(
        SqlWriter writer,
        KeysetTableContract keyset,
        PageKeysetSpec.SelectedPage selectedPage,
        string quotedDocIdCol
    )
    {
        if (writer.Dialect.Rules.Dialect is SqlDialect.Mssql)
        {
            AppendMssqlSelectedPageKeysetMaterialization(writer, keyset, quotedDocIdCol);
            return;
        }

        writer
            .Append("INSERT INTO ")
            .AppendRelation(keyset.Table)
            .Append(" (")
            .Append(quotedDocIdCol)
            .AppendLine(")")
            .AppendLine("VALUES");

        for (var index = 0; index < selectedPage.DocumentIds.Count; index++)
        {
            writer.Append("    (").AppendParameter(SelectedPageDocumentIdParameterName(index)).Append(")");
            writer.AppendLine(index + 1 < selectedPage.DocumentIds.Count ? "," : ";");
        }
    }

    private static void AppendMssqlSelectedPageKeysetMaterialization(
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
            .Append(", ")
            .AppendQuoted(HydrationSqlConventions.SelectedPageOrdinalColumnName)
            .AppendLine(")")
            .AppendLine("SELECT")
            .Append("    selected_document_ids.")
            .Append(quotedDocIdCol)
            .AppendLine(",")
            .Append("    selected_document_ids.")
            .AppendQuoted(HydrationSqlConventions.SelectedPageOrdinalColumnName)
            .AppendLine()
            .Append("FROM OPENJSON(")
            .AppendParameter(HydrationSqlConventions.SelectedPageDocumentIdsJsonParameterName)
            .AppendLine(")")
            .AppendLine("WITH (")
            .Append("    ")
            .Append(quotedDocIdCol)
            .AppendLine(" bigint '$.DocumentId',")
            .Append("    ")
            .AppendQuoted(HydrationSqlConventions.SelectedPageOrdinalColumnName)
            .AppendLine(" int '$.Ordinal'")
            .AppendLine(") selected_document_ids;");
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
        PlannedQueryParameterBinder.AddDbParameters(
            command,
            query.Plan,
            query.ParameterValues,
            "Hydration query keyset",
            "Hydration query keyset parameter",
            "Unsupported query-parameter binding kind."
        );
    }

    private static void AddSelectedPageParameters(DbCommand command, PageKeysetSpec.SelectedPage selectedPage)
    {
        if (selectedPage.DocumentIds.Count == 0)
        {
            return;
        }

        if (command is SqlCommand)
        {
            AddMssqlSelectedPageJsonParameter(command, selectedPage.DocumentIds);
            return;
        }

        for (var index = 0; index < selectedPage.DocumentIds.Count; index++)
        {
            AddScalarParameter(
                command,
                SelectedPageDocumentIdParameterName(index),
                selectedPage.DocumentIds[index]
            );
        }
    }

    private static void AddMssqlSelectedPageJsonParameter(DbCommand command, IReadOnlyList<long> documentIds)
    {
        var dbParameter = command.CreateParameter();
        dbParameter.ParameterName = $"@{HydrationSqlConventions.SelectedPageDocumentIdsJsonParameterName}";
        dbParameter.Value = HydrationSqlConventions.SerializeSelectedPageDocumentIds(documentIds);

        if (dbParameter is not SqlParameter sqlParameter)
        {
            throw new InvalidOperationException(
                "SQL Server selected-page hydration binding requires a SqlParameter instance."
            );
        }

        sqlParameter.SqlDbType = SqlDbType.NVarChar;
        sqlParameter.Size = -1;

        command.Parameters.Add(sqlParameter);
    }

    private static string SelectedPageDocumentIdParameterName(int index) => $"selectedDocumentId_{index}";

    private static void AddScalarParameter(DbCommand command, string bareName, object? value)
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
