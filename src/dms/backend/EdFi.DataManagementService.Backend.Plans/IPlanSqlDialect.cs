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
    /// Appends a dialect-specific paging clause.
    /// </summary>
    /// <param name="writer">The SQL writer to append to.</param>
    /// <param name="offsetParameterName">The bare offset parameter name.</param>
    /// <param name="limitParameterName">The bare limit parameter name.</param>
    void AppendPagingClause(SqlWriter writer, string offsetParameterName, string limitParameterName);

    /// <summary>
    /// Appends a dialect-specific <c>CREATE TEMP TABLE</c> DDL statement for the keyset table.
    /// </summary>
    /// <param name="writer">The SQL writer to append to.</param>
    /// <param name="keyset">The keyset table contract specifying table and column names.</param>
    void AppendCreateKeysetTempTable(SqlWriter writer, KeysetTableContract keyset);

    /// <summary>
    /// Appends a <c>SELECT</c> statement that joins <c>dms.Document</c> metadata to the
    /// materialized keyset table, returning document metadata columns for the page,
    /// ordered deterministically by <c>DocumentId</c>.
    /// </summary>
    /// <param name="writer">The SQL writer to append to.</param>
    /// <param name="keyset">The keyset table contract specifying table and column names.</param>
    void AppendDocumentMetadataSelect(SqlWriter writer, KeysetTableContract keyset);

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
/// Shared document metadata column names used by <see cref="IPlanSqlDialect.AppendDocumentMetadataSelect"/>
/// and consumed by ordinal in <see cref="HydrationReader.ReadDocumentMetadataAsync"/>.
/// </summary>
/// <remarks>
/// The ordinal positions defined here form a contract between the SQL emitter and the reader.
/// If columns are added, removed, or reordered, both sides must be updated together.
/// </remarks>
internal static class DocumentMetadataColumns
{
    // Ordinal 0 — supplied by the keyset contract (DocumentIdColumnName)
    public const string DocumentUuid = "DocumentUuid";
    public const string ContentVersion = "ContentVersion";
    public const string IdentityVersion = "IdentityVersion";
    public const string ContentLastModifiedAt = "ContentLastModifiedAt";
    public const string IdentityLastModifiedAt = "IdentityLastModifiedAt";

    /// <summary>
    /// Metadata column names in ordinal order (excluding DocumentId at ordinal 0).
    /// </summary>
    public static readonly string[] ColumnsInOrdinalOrder =
    [
        DocumentUuid,
        ContentVersion,
        IdentityVersion,
        ContentLastModifiedAt,
        IdentityLastModifiedAt,
    ];

    /// <summary>
    /// Appends the shared document metadata SELECT body using dialect-neutral quoting,
    /// including a deterministic <c>ORDER BY DocumentId</c>.
    /// </summary>
    internal static void AppendDocumentMetadataSelectBody(
        SqlWriter writer,
        KeysetTableContract keyset,
        DbTableName documentTable
    )
    {
        var quotedDocIdCol = writer.Dialect.QuoteIdentifier(keyset.DocumentIdColumnName.Value);

        writer.AppendLine("SELECT").Append("    d.").Append(quotedDocIdCol).AppendLine(",");

        for (var i = 0; i < ColumnsInOrdinalOrder.Length; i++)
        {
            writer.Append("    d.").Append(writer.Dialect.QuoteIdentifier(ColumnsInOrdinalOrder[i]));
            writer.AppendLine(i + 1 < ColumnsInOrdinalOrder.Length ? "," : "");
        }

        writer
            .Append("FROM ")
            .AppendTable(documentTable)
            .AppendLine(" d")
            .Append("INNER JOIN ")
            .AppendRelation(keyset.Table)
            .Append(" k ON d.")
            .Append(quotedDocIdCol)
            .Append(" = k.")
            .Append(quotedDocIdCol)
            .AppendLine()
            .Append("ORDER BY d.")
            .Append(quotedDocIdCol)
            .AppendLine(";");
    }
}
