// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

internal static class OrderedDeleteCommandBuilder
{
    private static readonly DbColumnName DocumentIdColumn = new("DocumentId");

    public static RelationalCommand BuildResourceDeleteByDocumentIdCommand(
        SqlDialect dialect,
        DbTableName rootTable,
        long documentId
    )
    {
        var rootDeleteSql = $"""
            DELETE FROM {FormatTable(dialect, rootTable)}
            WHERE {FormatColumn(dialect, DocumentIdColumn)} = @documentId;
            """;

        return new RelationalCommand(
            $"{rootDeleteSql}{Environment.NewLine}{BuildDocumentDeleteByDocumentIdSql(dialect)}",
            [new RelationalParameter("@documentId", documentId)]
        );
    }

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
