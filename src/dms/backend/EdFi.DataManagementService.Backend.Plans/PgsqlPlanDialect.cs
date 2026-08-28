// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// PostgreSQL-specific plan/query dialect helpers.
/// </summary>
internal sealed class PgsqlPlanDialect : IPlanSqlDialect
{
    private static readonly DbTableName DocumentTable = new(new DbSchemaName("dms"), "Document");

    /// <inheritdoc />
    public SqlDialect Dialect => SqlDialect.Pgsql;

    /// <inheritdoc />
    public string DisplayName => "PostgreSQL";

    /// <inheritdoc />
    public bool SupportsSingleDocumentHydration => true;

    /// <inheritdoc />
    public string CorrelatedRowSetJoinKeyword => "CROSS JOIN LATERAL";

    /// <summary>
    /// Appends a PostgreSQL <c>LIMIT</c>/<c>OFFSET</c> paging clause.
    /// </summary>
    /// <param name="writer">The SQL writer to append to.</param>
    /// <param name="offsetParameterName">The bare offset parameter name.</param>
    /// <param name="limitParameterName">The bare limit parameter name.</param>
    public void AppendPagingClause(SqlWriter writer, string offsetParameterName, string limitParameterName)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer
            .Append("LIMIT ")
            .AppendParameter(limitParameterName)
            .Append(" OFFSET ")
            .AppendParameter(offsetParameterName)
            .AppendLine();
    }

    /// <summary>
    /// Emits nothing: PostgreSQL limits cursor pages in a trailing <c>LIMIT</c> clause.
    /// </summary>
    /// <param name="writer">The SQL writer to append to.</param>
    /// <param name="pageSizeParameterName">The bare cursor page size parameter name.</param>
    public void AppendCursorSelectRowLimitPrefix(SqlWriter writer, string pageSizeParameterName)
    {
        ArgumentNullException.ThrowIfNull(writer);
    }

    /// <summary>
    /// Appends a PostgreSQL <c>LIMIT</c> clause with no offset operation.
    /// </summary>
    /// <param name="writer">The SQL writer to append to.</param>
    /// <param name="pageSizeParameterName">The bare cursor page size parameter name.</param>
    public void AppendCursorPagingClause(SqlWriter writer, string pageSizeParameterName)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.Append("LIMIT ").AppendParameter(pageSizeParameterName).AppendLine();
    }

    /// <inheritdoc />
    public string CandidateCountOverWindowSql => "COUNT(*) OVER ()";

    /// <summary>
    /// Appends the PostgreSQL partition-size expression.
    /// </summary>
    /// <remarks>
    /// <c>numeric</c> is arbitrary-precision, so the division cannot truncate or overflow at any
    /// candidate count a <c>bigint</c> identity can reach. The ceiling is converted to <c>bigint</c>
    /// before <c>GREATEST</c> so both arguments, and therefore the result, are integers: casting a
    /// <c>numeric</c> to <c>bigint</c> rounds rather than truncates, which is exact here only because
    /// <c>CEIL</c> has already produced an integral value.
    /// </remarks>
    /// <param name="writer">The SQL writer to append to.</param>
    /// <param name="candidateCountExpression">The already-qualified candidate count expression.</param>
    /// <param name="partitionCountParameterName">The bare requested partition count parameter name.</param>
    /// <param name="minimumPartitionSizeParameterName">The bare minimum partition size parameter name.</param>
    public void AppendPartitionSizeExpression(
        SqlWriter writer,
        string candidateCountExpression,
        string partitionCountParameterName,
        string minimumPartitionSizeParameterName
    )
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateCountExpression);

        writer
            .Append($"GREATEST(CAST(CEIL(CAST({candidateCountExpression} AS numeric) / CAST(")
            .AppendParameter(partitionCountParameterName)
            .Append(" AS numeric)) AS bigint), ")
            .AppendParameter(minimumPartitionSizeParameterName)
            .Append(")");
    }

    /// <inheritdoc />
    public void AppendCreateKeysetTempTable(
        SqlWriter writer,
        KeysetTableContract keyset,
        bool includeAnchorColumn = false
    )
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(keyset);

        writer
            .Append("DROP TABLE IF EXISTS ")
            .AppendRelation(keyset.Table)
            .AppendLine(";")
            .Append("CREATE TEMP TABLE ")
            .AppendRelation(keyset.Table)
            .Append(" (")
            .AppendQuoted(keyset.DocumentIdColumnName.Value)
            .Append(" bigint PRIMARY KEY, ")
            .AppendQuoted(HydrationSqlConventions.SelectedPageOrdinalColumnName)
            .Append(" int NULL");

        if (includeAnchorColumn)
        {
            writer
                .Append(", ")
                .AppendQuoted(HydrationSqlConventions.SelectedAnchorColumnName)
                .Append(" bigint NULL");
        }

        writer.AppendLine(") ON COMMIT DROP;");
    }

    /// <summary>
    /// Emits nothing: PostgreSQL returns inserted keyset ids from a trailing <c>RETURNING</c> clause.
    /// </summary>
    /// <param name="writer">The SQL writer to append to.</param>
    /// <param name="keyset">The keyset table contract specifying table and column names.</param>
    public void AppendKeysetSelectedIdOutputClause(
        SqlWriter writer,
        KeysetTableContract keyset,
        bool includeAnchorColumn = false
    )
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(keyset);
    }

    /// <summary>
    /// Appends a PostgreSQL <c>RETURNING</c> clause naming the keyset document-id column.
    /// </summary>
    /// <param name="writer">The SQL writer to append to.</param>
    /// <param name="keyset">The keyset table contract specifying table and column names.</param>
    public void AppendKeysetSelectedIdReturningClause(
        SqlWriter writer,
        KeysetTableContract keyset,
        bool includeAnchorColumn = false
    )
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(keyset);

        writer.Append(" RETURNING ").AppendQuoted(keyset.DocumentIdColumnName.Value);

        if (includeAnchorColumn)
        {
            writer.Append(", ").AppendQuoted(HydrationSqlConventions.SelectedAnchorColumnName);
        }
    }

    /// <inheritdoc />
    public void AppendDocumentMetadataSelect(SqlWriter writer, KeysetTableContract keyset)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(keyset);

        DocumentMetadataColumns.AppendDocumentMetadataSelectBody(
            writer,
            keyset,
            DocumentTable,
            HydrationSqlConventions.SelectedPageOrdinalColumnName
        );
    }

    /// <inheritdoc />
    public void AppendSingleDocumentMetadataSelect(SqlWriter writer, string documentIdParameterName)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(documentIdParameterName);

        DocumentMetadataColumns.AppendSingleDocumentMetadataSelectBody(
            writer,
            DocumentTable,
            documentIdParameterName
        );
    }

    /// <inheritdoc />
    public void AppendComparisonSql(
        SqlWriter writer,
        string tableAlias,
        DbColumnName column,
        string operatorToken,
        string parameterName,
        ScalarKind? scalarKind
    )
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(tableAlias);
        ArgumentNullException.ThrowIfNull(operatorToken);
        ArgumentNullException.ThrowIfNull(parameterName);

        writer
            .Append($"{tableAlias}.")
            .AppendQuoted(column.Value)
            .Append(" ")
            .Append(operatorToken)
            .Append(" ")
            .AppendParameter(parameterName);
    }
}
