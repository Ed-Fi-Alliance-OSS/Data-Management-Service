// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// SQL Server-specific plan/query dialect helpers.
/// </summary>
internal sealed class MssqlPlanDialect : IPlanSqlDialect
{
    private static readonly DbTableName DocumentTable = new(new DbSchemaName("dms"), "Document");
    private const string BinaryStringEqualityCollation = "Latin1_General_100_BIN2";

    /// <inheritdoc />
    public SqlDialect Dialect => SqlDialect.Mssql;

    /// <inheritdoc />
    public string DisplayName => "SQL Server";

    /// <inheritdoc />
    public bool SupportsSingleDocumentHydration => true;

    /// <inheritdoc />
    public string CorrelatedRowSetJoinKeyword => "CROSS APPLY";

    /// <summary>
    /// Appends a SQL Server <c>OFFSET</c>/<c>FETCH NEXT</c> paging clause.
    /// </summary>
    /// <remarks>
    /// SQL Server requires an <c>ORDER BY</c> clause when using <c>OFFSET</c>/<c>FETCH</c>.
    /// </remarks>
    /// <param name="writer">The SQL writer to append to.</param>
    /// <param name="offsetParameterName">The bare offset parameter name.</param>
    /// <param name="limitParameterName">The bare limit parameter name.</param>
    public void AppendPagingClause(SqlWriter writer, string offsetParameterName, string limitParameterName)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer
            .Append("OFFSET ")
            .AppendParameter(offsetParameterName)
            .Append(" ROWS FETCH NEXT ")
            .AppendParameter(limitParameterName)
            .AppendLine(" ROWS ONLY");
    }

    /// <summary>
    /// Appends a SQL Server <c>TOP (@pageSize) </c> prefix inside the <c>SELECT</c> list.
    /// </summary>
    /// <remarks>
    /// Using <c>TOP</c> rather than <c>OFFSET 0</c> keeps the no-offset invariant literal rather than
    /// merely semantic, and it is also what makes the accompanying <c>ORDER BY</c> legal when this
    /// statement is wrapped in a common table expression.
    /// </remarks>
    /// <param name="writer">The SQL writer to append to.</param>
    /// <param name="pageSizeParameterName">The bare cursor page size parameter name.</param>
    public void AppendCursorSelectRowLimitPrefix(SqlWriter writer, string pageSizeParameterName)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.Append("TOP (").AppendParameter(pageSizeParameterName).Append(") ");
    }

    /// <summary>
    /// Emits nothing: SQL Server has already limited the cursor page in the <c>SELECT</c> list.
    /// </summary>
    /// <param name="writer">The SQL writer to append to.</param>
    /// <param name="pageSizeParameterName">The bare cursor page size parameter name.</param>
    public void AppendCursorPagingClause(SqlWriter writer, string pageSizeParameterName)
    {
        ArgumentNullException.ThrowIfNull(writer);
    }

    /// <inheritdoc />
    public string CandidateCountOverWindowSql => "COUNT_BIG(*) OVER ()";

    /// <summary>
    /// Appends the SQL Server partition-size expression.
    /// </summary>
    /// <remarks>
    /// <c>CASE</c> rather than <c>GREATEST</c>, which requires SQL Server 2022 or later: nothing in this
    /// repository establishes that floor for a deployment, and a partition endpoint is not the place to
    /// introduce a minimum-version requirement. The ceiling expression therefore appears twice, which is
    /// a scalar recomputation over an already-materialized row rather than a second pass over the
    /// candidate set.
    ///
    /// <c>decimal(28,0) / decimal(10,0)</c> yields <c>decimal(38,10)</c>: the division rule asks for
    /// scale <c>max(6, s1 + p2 + 1)</c> = 11 at precision <c>p1 - s1 + s2 + scale</c> = 39, which exceeds
    /// the 38 maximum, so precision caps at 38 and scale drops to 10. That leaves 28 integral digits,
    /// above the 19 a <c>bigint</c> count can need, so the quotient cannot lose an integral digit. Both
    /// operands are cast explicitly so the result type does not depend on how a driver infers a
    /// parameter's type.
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

        void AppendCeiling()
        {
            writer
                .Append($"CAST(CEILING(CAST({candidateCountExpression} AS decimal(28,0)) / CAST(")
                .AppendParameter(partitionCountParameterName)
                .Append(" AS decimal(10,0))) AS bigint)");
        }

        writer.Append("CASE WHEN ");
        AppendCeiling();
        writer.Append(" > ").AppendParameter(minimumPartitionSizeParameterName).Append(" THEN ");
        AppendCeiling();
        writer.Append(" ELSE ").AppendParameter(minimumPartitionSizeParameterName).Append(" END");
    }

    /// <inheritdoc />
    public void AppendCreateKeysetTempTable(SqlWriter writer, KeysetTableContract keyset)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(keyset);

        writer
            .Append("IF OBJECT_ID('tempdb..")
            .AppendRelation(keyset.Table)
            .AppendLine("') IS NOT NULL")
            .Append("    DROP TABLE ")
            .AppendRelation(keyset.Table)
            .AppendLine(";")
            .Append("CREATE TABLE ")
            .AppendRelation(keyset.Table)
            .Append(" (")
            .AppendQuoted(keyset.DocumentIdColumnName.Value)
            .Append(" bigint PRIMARY KEY, ")
            .AppendQuoted(HydrationSqlConventions.SelectedPageOrdinalColumnName)
            .AppendLine(" int NULL);");
    }

    /// <summary>
    /// Appends a SQL Server <c>OUTPUT INSERTED</c> clause naming the keyset document-id column.
    /// </summary>
    /// <param name="writer">The SQL writer to append to.</param>
    /// <param name="keyset">The keyset table contract specifying table and column names.</param>
    public void AppendKeysetSelectedIdOutputClause(SqlWriter writer, KeysetTableContract keyset)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(keyset);

        writer.Append("OUTPUT INSERTED.").AppendQuoted(keyset.DocumentIdColumnName.Value).AppendLine();
    }

    /// <summary>
    /// Emits nothing: SQL Server has already returned the inserted keyset ids from the insert's
    /// <c>OUTPUT</c> clause.
    /// </summary>
    /// <param name="writer">The SQL writer to append to.</param>
    /// <param name="keyset">The keyset table contract specifying table and column names.</param>
    public void AppendKeysetSelectedIdReturningClause(SqlWriter writer, KeysetTableContract keyset)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(keyset);
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

        writer.Append($"{tableAlias}.").AppendQuoted(column.Value);

        if (scalarKind == ScalarKind.String && string.Equals(operatorToken, "=", StringComparison.Ordinal))
        {
            writer.Append($" COLLATE {BinaryStringEqualityCollation}");
        }

        writer.Append(" ").Append(operatorToken).Append(" ").AppendParameter(parameterName);
    }
}
