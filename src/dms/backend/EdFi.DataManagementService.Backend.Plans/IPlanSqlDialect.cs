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
    /// Appends a <c>SELECT</c> statement that reads <c>dms.Document</c> metadata for a single
    /// document id parameter, returning the same metadata columns and order as the keyset form.
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
    public const string IdentityVersion = "IdentityVersion";
    public const string ContentLastModifiedAt = "ContentLastModifiedAt";
    public const string IdentityLastModifiedAt = "IdentityLastModifiedAt";

    /// <summary>
    /// Metadata column names in reader ordinal order.
    /// </summary>
    public static readonly string[] ColumnsInOrdinalOrder =
    [
        DocumentId,
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
            .Append("ORDER BY d.")
            .Append(quotedDocumentIdColumn)
            .AppendLine(";");
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
