// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Builds the single-statement resource and descriptor deletes. Each one deletes the resource root row (or
/// the <c>dms.Descriptor</c> row) and returns the deleted <c>DocumentId</c>, which is the affected-rows
/// signal <see cref="RelationalDeleteExecution"/> reads: a returned row means the target existed, no row
/// means it did not.
/// </summary>
/// <remarks>
/// There is no ordering question left. Deleting a document used to take two statements — the root row and
/// then the <c>dms.Document</c> row, whose <c>RETURNING</c>/<c>OUTPUT DELETED</c> carried the signal — and
/// child rows still cascade off the root row's own foreign keys. The root row is now the only row a delete
/// touches, so it carries the signal itself.
/// <para>
/// The signal shape differs by dialect only because SQL Server rejects a plain <c>OUTPUT</c> on a
/// trigger-bearing table, and every root table plus <c>dms.Descriptor</c> carries the stamping trigger. The
/// deleted id therefore lands in a table variable and a trailing <c>SELECT</c> exposes it as the row the
/// executor scans for.
/// </para>
/// </remarks>
internal static class OrderedDeleteCommandBuilder
{
    /// <summary>
    /// The SQL Server table variable carrying the deleted <c>DocumentId</c> out of the <c>OUTPUT</c>
    /// clause.
    /// </summary>
    private const string DeletedDocumentIdTableVariable = "@deletedDocumentId";

    private static readonly DbColumnName DocumentIdColumn = new("DocumentId");

    public static RelationalCommand BuildResourceDeleteByDocumentIdCommand(
        SqlDialect dialect,
        DbTableName rootTable,
        long documentId
    )
    {
        var table = FormatTable(dialect, rootTable);
        var documentIdColumn = FormatColumn(dialect, DocumentIdColumn);

        var sql = dialect switch
        {
            SqlDialect.Pgsql => $"""
                DELETE FROM {table}
                WHERE {documentIdColumn} = @documentId
                RETURNING {documentIdColumn};
                """,
            SqlDialect.Mssql => $"""
                DECLARE {DeletedDocumentIdTableVariable} TABLE ({documentIdColumn} bigint);

                DELETE FROM {table}
                OUTPUT DELETED.{documentIdColumn} INTO {DeletedDocumentIdTableVariable}
                WHERE {documentIdColumn} = @documentId;

                SELECT {documentIdColumn} FROM {DeletedDocumentIdTableVariable};
                """,
            _ => throw new NotSupportedException(
                $"Relational delete does not support SQL dialect '{dialect}'."
            ),
        };

        return new RelationalCommand(sql, [new RelationalParameter("@documentId", documentId)]);
    }

    /// <summary>
    /// Builds the descriptor delete. It seeks the descriptor row's own <c>UX_Descriptor_DocumentUuid</c>
    /// index and carries the descriptor row's <c>ResourceKeyId</c> mirror as the residual scoping
    /// predicate, so no <c>dms.Document</c> read is involved, and returns the deleted <c>DocumentId</c> as
    /// the affected-rows signal.
    /// </summary>
    public static RelationalCommand BuildDescriptorDeleteCommand(
        SqlDialect dialect,
        DocumentUuid documentUuid,
        short resourceKeyId
    ) =>
        dialect switch
        {
            SqlDialect.Pgsql => new RelationalCommand(
                """
                DELETE FROM dms."Descriptor"
                WHERE "DocumentUuid" = @documentUuid
                  AND "ResourceKeyId" = @resourceKeyId
                RETURNING "DocumentId";
                """,
                [
                    new RelationalParameter("@documentUuid", documentUuid.Value),
                    new RelationalParameter("@resourceKeyId", resourceKeyId),
                ]
            ),
            SqlDialect.Mssql => new RelationalCommand(
                """
                DECLARE @deletedDocumentId TABLE ([DocumentId] bigint);

                DELETE FROM [dms].[Descriptor]
                OUTPUT DELETED.[DocumentId] INTO @deletedDocumentId
                WHERE [DocumentUuid] = @documentUuid
                  AND [ResourceKeyId] = @resourceKeyId;

                SELECT [DocumentId] FROM @deletedDocumentId;
                """,
                [
                    new RelationalParameter("@documentUuid", documentUuid.Value),
                    new RelationalParameter("@resourceKeyId", resourceKeyId),
                ]
            ),
            _ => throw new NotSupportedException(
                $"Descriptor delete does not support SQL dialect '{dialect}'."
            ),
        };

    private static string FormatTable(SqlDialect dialect, DbTableName table) =>
        $"{QuoteIdentifier(dialect, table.Schema.Value)}.{QuoteIdentifier(dialect, table.Name)}";

    private static string FormatColumn(SqlDialect dialect, DbColumnName column) =>
        QuoteIdentifier(dialect, column.Value);

    private static string QuoteIdentifier(SqlDialect dialect, string identifier) =>
        dialect switch
        {
            SqlDialect.Pgsql => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"",
            SqlDialect.Mssql => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]",
            _ => throw new NotSupportedException(
                $"Identifier quoting does not support SQL dialect '{dialect}'."
            ),
        };
}
