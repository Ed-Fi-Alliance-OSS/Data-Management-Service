// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

internal static class OrderedDeleteCommandBuilder
{
    private const string DocumentIdParameterName = "@documentId";

    private static readonly DbColumnName DocumentIdColumn = new("DocumentId");

    public static RelationalCommand BuildResourceDeleteByDocumentIdCommand(
        SqlDialect dialect,
        DbTableName rootTable,
        long documentId
    ) =>
        new(
            BuildResourceRootDeleteByDocumentIdCommand(dialect, rootTable, documentId).CommandText
                + Environment.NewLine
                + BuildDocumentDeleteByDocumentIdSql(dialect),
            [new RelationalParameter(DocumentIdParameterName, documentId)]
        );

    /// <summary>
    /// The resource root delete alone. Split out from the combined command so a co-batched delete can emit
    /// it as its own logical statement: it modifies a table carrying an emitted <c>*_Stamp</c> trigger, so
    /// it cannot use <c>OUTPUT</c> and needs the builder's sentinel to own a result set.
    /// </summary>
    public static RelationalCommand BuildResourceRootDeleteByDocumentIdCommand(
        SqlDialect dialect,
        DbTableName rootTable,
        long documentId
    ) =>
        new(
            $"""
            DELETE FROM {FormatTable(dialect, rootTable)}
            WHERE {FormatColumn(dialect, DocumentIdColumn)} = {DocumentIdParameterName};
            """,
            [new RelationalParameter(DocumentIdParameterName, documentId)]
        );

    /// <summary>
    /// The <c>dms.Document</c> delete alone, returning the deleted id. It carries no trigger, which is the
    /// only reason this one statement can use <c>RETURNING</c> / <c>OUTPUT</c>; that is a property of the
    /// current DDL and is not relied on for resource tables.
    /// </summary>
    public static RelationalCommand BuildDocumentDeleteByDocumentIdCommand(
        SqlDialect dialect,
        long documentId
    ) =>
        new(
            BuildDocumentDeleteByDocumentIdSql(dialect),
            [new RelationalParameter(DocumentIdParameterName, documentId)]
        );

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
                WHERE "DocumentId" IN (
                    SELECT "DocumentId"
                    FROM dms."Document"
                    WHERE "DocumentUuid" = @documentUuid
                      AND "ResourceKeyId" = @resourceKeyId
                );

                DELETE FROM dms."Document"
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
                DELETE FROM [dms].[Descriptor]
                WHERE [DocumentId] IN (
                    SELECT [DocumentId]
                    FROM [dms].[Document]
                    WHERE [DocumentUuid] = @documentUuid
                      AND [ResourceKeyId] = @resourceKeyId
                );

                DELETE FROM [dms].[Document]
                OUTPUT DELETED.[DocumentId]
                WHERE [DocumentUuid] = @documentUuid
                  AND [ResourceKeyId] = @resourceKeyId;
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

    private static string BuildDocumentDeleteByDocumentIdSql(SqlDialect dialect) =>
        dialect switch
        {
            SqlDialect.Pgsql => """
                DELETE FROM dms."Document"
                WHERE "DocumentId" = @documentId
                RETURNING "DocumentId";
                """,
            SqlDialect.Mssql => """
                DELETE FROM [dms].[Document]
                OUTPUT DELETED.[DocumentId]
                WHERE [DocumentId] = @documentId;
                """,
            _ => throw new NotSupportedException(
                $"Relational delete does not support SQL dialect '{dialect}'."
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
