// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Dialect-specific plan/query SQL emission helpers.
/// </summary>
internal interface IPlanSqlDialect
{
    /// <summary>
    /// Gets the backend SQL dialect.
    /// </summary>
    SqlDialect Dialect { get; }

    /// <summary>
    /// Gets the dialect display name used in diagnostics.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Gets whether this dialect supports single-document hydration batches without keyset
    /// table materialization.
    /// </summary>
    bool SupportsSingleDocumentHydration { get; }

    /// <summary>
    /// Gets the dialect keyword that joins a correlated inline row set (a <c>VALUES</c> list
    /// referencing columns of a preceding FROM-clause source) to that source.
    /// </summary>
    string CorrelatedRowSetJoinKeyword { get; }

    /// <summary>
    /// Appends a dialect-specific paging clause.
    /// </summary>
    /// <param name="writer">The SQL writer to append to.</param>
    /// <param name="offsetParameterName">The bare offset parameter name.</param>
    /// <param name="limitParameterName">The bare limit parameter name.</param>
    void AppendPagingClause(SqlWriter writer, string offsetParameterName, string limitParameterName);

    /// <summary>
    /// Appends a dialect-specific row-limit prefix inside the <c>SELECT</c> list for cursor page
    /// selection. SQL Server emits <c>TOP (@pageSize) </c> here; PostgreSQL limits in a trailing
    /// clause and emits nothing.
    /// </summary>
    /// <param name="writer">The SQL writer to append to.</param>
    /// <param name="pageSizeParameterName">The bare cursor page size parameter name.</param>
    void AppendCursorSelectRowLimitPrefix(SqlWriter writer, string pageSizeParameterName);

    /// <summary>
    /// Appends a dialect-specific trailing size clause for cursor page selection. PostgreSQL emits
    /// <c>LIMIT @pageSize</c>; SQL Server has already limited in the <c>SELECT</c> list and emits
    /// nothing. Neither dialect emits an offset, which is what keeps cursor cost independent of depth.
    /// </summary>
    /// <param name="writer">The SQL writer to append to.</param>
    /// <param name="pageSizeParameterName">The bare cursor page size parameter name.</param>
    void AppendCursorPagingClause(SqlWriter writer, string pageSizeParameterName);

    /// <summary>
    /// Gets the dialect's window aggregate that counts every row of the partition candidate relation.
    /// SQL Server uses <c>COUNT_BIG</c>, whose <c>bigint</c> result a candidate set larger than an
    /// <c>int</c> requires; PostgreSQL's <c>COUNT</c> already returns <c>bigint</c>.
    /// </summary>
    string CandidateCountOverWindowSql { get; }

    /// <summary>
    /// Appends the dialect's partition-size expression: the greater of the mathematical ceiling of
    /// <paramref name="candidateCountExpression" /> divided by the requested partition count, and the
    /// minimum partition size, as a <c>bigint</c>.
    /// </summary>
    /// <remarks>
    /// The division must be performed in a non-integer type. An integer quotient with a ceiling applied
    /// afterward is a no-op on an already-truncated value, which produces partitions smaller than the
    /// requested count requires and therefore one token more than the contract permits. The result is
    /// converted back to <c>bigint</c> so that the modulo that selects start rows has operands of the
    /// same type as <c>ROW_NUMBER()</c>.
    /// </remarks>
    /// <param name="writer">The SQL writer to append to.</param>
    /// <param name="candidateCountExpression">
    /// The already-qualified expression yielding the candidate count.
    /// </param>
    /// <param name="partitionCountParameterName">The bare requested partition count parameter name.</param>
    /// <param name="minimumPartitionSizeParameterName">
    /// The bare minimum partition size parameter name.
    /// </param>
    void AppendPartitionSizeExpression(
        SqlWriter writer,
        string candidateCountExpression,
        string partitionCountParameterName,
        string minimumPartitionSizeParameterName
    );

    /// <summary>
    /// Appends a dialect-specific <c>CREATE TEMP TABLE</c> DDL statement for the keyset table.
    /// </summary>
    /// <param name="writer">The SQL writer to append to.</param>
    /// <param name="keyset">The keyset table contract specifying table and column names.</param>
    /// <param name="includeAnchorColumn">
    /// Adds the nullable continuation-anchor column to the table. Set only for a
    /// <c>ContentVersion</c>-anchored query keyset, so every other batch emits the DDL it always has.
    /// </param>
    void AppendCreateKeysetTempTable(
        SqlWriter writer,
        KeysetTableContract keyset,
        bool includeAnchorColumn = false
    );

    /// <summary>
    /// Appends a dialect-specific clause that returns the values a query keyset materialization
    /// inserted, positioned between the insert column list and the row source. SQL Server emits
    /// <c>OUTPUT INSERTED.[DocumentId]</c> here; PostgreSQL returns them from a trailing clause and
    /// emits nothing.
    /// </summary>
    /// <param name="writer">The SQL writer to append to.</param>
    /// <param name="keyset">The keyset table contract specifying table and column names.</param>
    /// <param name="includeAnchorColumn">
    /// Also returns the continuation-anchor column. Must match what the insert column list and the
    /// table DDL carry, or the statement names a column the keyset table does not have.
    /// </param>
    void AppendKeysetSelectedIdOutputClause(
        SqlWriter writer,
        KeysetTableContract keyset,
        bool includeAnchorColumn = false
    );

    /// <summary>
    /// Appends a dialect-specific trailing clause that returns the values a query keyset
    /// materialization inserted. PostgreSQL emits <c>RETURNING "DocumentId"</c>; SQL Server has
    /// already returned them from the insert's <c>OUTPUT</c> clause and emits nothing.
    /// </summary>
    /// <param name="writer">The SQL writer to append to.</param>
    /// <param name="keyset">The keyset table contract specifying table and column names.</param>
    /// <param name="includeAnchorColumn">
    /// Also returns the continuation-anchor column. Must match what the insert column list and the
    /// table DDL carry, or the statement names a column the keyset table does not have.
    /// </param>
    void AppendKeysetSelectedIdReturningClause(
        SqlWriter writer,
        KeysetTableContract keyset,
        bool includeAnchorColumn = false
    );

    /// <summary>
    /// Appends a <c>SELECT</c> statement that joins <c>dms.Document</c> metadata to the
    /// materialized keyset table, returning document metadata columns for the page,
    /// ordered by selected-page ordinal when available, otherwise deterministically by
    /// <c>DocumentId</c>.
    /// </summary>
    /// <param name="writer">The SQL writer to append to.</param>
    /// <param name="keyset">The keyset table contract specifying table and column names.</param>
    void AppendDocumentMetadataSelect(SqlWriter writer, KeysetTableContract keyset);

    /// <summary>
    /// Appends a <c>SELECT</c> statement that returns metadata for a single document id parameter.
    /// </summary>
    /// <param name="writer">The SQL writer to append to.</param>
    /// <param name="documentIdParameterName">The bare document id parameter name.</param>
    void AppendSingleDocumentMetadataSelect(SqlWriter writer, string documentIdParameterName);

    /// <summary>
    /// Appends a predicate comparison against the supplied table alias and column.
    /// </summary>
    /// <param name="writer">The SQL writer to append to.</param>
    /// <param name="tableAlias">The already-validated SQL table alias.</param>
    /// <param name="column">The compared column.</param>
    /// <param name="operatorToken">The SQL operator token.</param>
    /// <param name="parameterName">The bare SQL parameter name.</param>
    /// <param name="scalarKind">
    /// Optional scalar-kind metadata for the compared value, used for provider-specific text-comparison behavior.
    /// </param>
    void AppendComparisonSql(
        SqlWriter writer,
        string tableAlias,
        DbColumnName column,
        string operatorToken,
        string parameterName,
        ScalarKind? scalarKind
    );
}

/// <summary>
/// Shared document metadata column names used by metadata SELECT emitters and consumed by ordinal
/// in <see cref="HydrationReader.ReadDocumentMetadataAsync"/>.
/// </summary>
/// <remarks>
/// The ordinal positions defined here form a contract between the SQL emitter and the reader.
/// If columns are added, removed, or reordered, both sides must be updated together.
/// </remarks>
internal static class DocumentMetadataColumns
{
    public const string DocumentId = "DocumentId";
    public const string DocumentUuid = "DocumentUuid";
    public const string ContentVersion = "ContentVersion";
    public const string ContentLastModifiedAt = "ContentLastModifiedAt";
    public const string ResourceKeyId = "ResourceKeyId";

    /// <summary>
    /// Metadata column names in reader ordinal order.
    /// </summary>
    public static readonly string[] ColumnsInOrdinalOrder =
    [
        DocumentId,
        DocumentUuid,
        ContentVersion,
        ContentLastModifiedAt,
        ResourceKeyId,
    ];

    /// <summary>
    /// Appends the shared document metadata SELECT body using dialect-neutral quoting. When an
    /// ordinal column is available on the keyset table, selected pages retain that order; otherwise
    /// rows are ordered deterministically by <c>DocumentId</c>.
    /// </summary>
    internal static void AppendDocumentMetadataSelectBody(
        SqlWriter writer,
        KeysetTableContract keyset,
        DbTableName documentTable,
        string? keysetOrdinalColumnName = null
    )
    {
        var quotedDocumentIdColumn = writer.Dialect.QuoteIdentifier(DocumentId);
        var quotedKeysetDocumentIdColumn = writer.Dialect.QuoteIdentifier(keyset.DocumentIdColumnName.Value);

        AppendSelectList(writer);

        writer
            .Append("FROM ")
            .AppendTable(documentTable)
            .AppendLine(" d")
            .Append("INNER JOIN ")
            .AppendRelation(keyset.Table)
            .Append(" k ON d.")
            .Append(quotedDocumentIdColumn)
            .Append(" = k.")
            .Append(quotedKeysetDocumentIdColumn)
            .AppendLine()
            .Append("ORDER BY ");

        if (keysetOrdinalColumnName is not null)
        {
            writer
                .Append("COALESCE(k.")
                .Append(writer.Dialect.QuoteIdentifier(keysetOrdinalColumnName))
                .Append(", d.")
                .Append(quotedDocumentIdColumn)
                .Append("), d.");
        }
        else
        {
            writer.Append("d.");
        }

        writer.Append(quotedDocumentIdColumn).AppendLine(";");
    }

    /// <summary>
    /// Appends the shared document metadata SELECT body for a single document id parameter,
    /// including a deterministic <c>ORDER BY DocumentId</c>.
    /// </summary>
    internal static void AppendSingleDocumentMetadataSelectBody(
        SqlWriter writer,
        DbTableName documentTable,
        string documentIdParameterName
    )
    {
        var quotedDocumentIdColumn = writer.Dialect.QuoteIdentifier(DocumentId);

        AppendSelectList(writer);

        writer
            .Append("FROM ")
            .AppendTable(documentTable)
            .AppendLine(" d")
            .Append("WHERE d.")
            .Append(quotedDocumentIdColumn)
            .Append(" = ")
            .AppendParameter(documentIdParameterName)
            .AppendLine()
            .Append("ORDER BY d.")
            .Append(quotedDocumentIdColumn)
            .AppendLine(";");
    }

    private static void AppendSelectList(SqlWriter writer)
    {
        writer.AppendLine("SELECT");

        for (var i = 0; i < ColumnsInOrdinalOrder.Length; i++)
        {
            writer.Append("    d.").Append(writer.Dialect.QuoteIdentifier(ColumnsInOrdinalOrder[i]));
            writer.AppendLine(i + 1 < ColumnsInOrdinalOrder.Length ? "," : "");
        }
    }
}
