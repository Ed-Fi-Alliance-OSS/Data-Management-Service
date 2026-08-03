// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.RelationalModel.Naming;

namespace EdFi.DataManagementService.Backend;

internal static class RelationalDocumentLockCommandBuilder
{
    private const string DocumentIdParameterName = "@documentId";

    /// <summary>
    /// Builds the row-lock statement that pins one document's <c>ContentVersion</c> for the remainder of
    /// the write transaction.
    /// </summary>
    /// <remarks>
    /// <paramref name="lockTable"/> names the row to lock. Non-descriptor writes pass the resource root
    /// table so the locked row is the same row every later read of that document's metadata resolves —
    /// the current-state load, the freshness re-check, and the post-persist stamp read all source the
    /// root row's trigger-maintained document-metadata mirrors. Locking a row in one table while
    /// comparing stamps read from another would split the guarded-no-op comparison across two tables.
    /// <para>
    /// <b>Resolved ordering hazard (historical note).</b> While the write path still wrote
    /// <c>dms.Document</c>, locking the root row inverted this writer's lock order relative to the
    /// stamping/propagation cascades: the writer took <c>Root(D)</c> first and reached
    /// <c>dms.Document(D)</c> only later through the stamping trigger, while a cascade triggered by
    /// another transaction took <c>dms.Document(D)</c> before <c>Root(D)</c>. That narrow contention
    /// pattern could deadlock, and the resilience pipeline absorbed it (SQL Server 1205, PostgreSQL 40P01
    /// classify as transient and replay the whole write). The write path no longer writes
    /// <c>dms.Document</c> at all — <c>DocumentId</c> comes from <c>dms.DocumentIdSequence</c>, the root
    /// insert returns it, and the delete touches only the root row — so the root row is now the only row
    /// either side takes and the cycle does not exist.
    /// </para>
    /// </remarks>
    public static RelationalCommand BuildContentVersionCommand(
        SqlDialect dialect,
        DbTableName lockTable,
        long documentId
    )
    {
        if (dialect is not (SqlDialect.Pgsql or SqlDialect.Mssql))
        {
            throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null);
        }

        var contentVersionColumn = SqlIdentifierQuoter.QuoteIdentifier(
            dialect,
            RelationalNameConventions.ContentVersionColumnName
        );
        var documentIdColumn = SqlIdentifierQuoter.QuoteIdentifier(
            dialect,
            RelationalNameConventions.DocumentIdColumnName
        );
        var quotedTable = SqlIdentifierQuoter.QuoteTableName(dialect, lockTable);

        return dialect switch
        {
            SqlDialect.Pgsql => new RelationalCommand(
                $"""
                SELECT
                    document.{contentVersionColumn} AS {contentVersionColumn}
                FROM {quotedTable} document
                WHERE document.{documentIdColumn} = @documentId
                FOR UPDATE
                """,
                [new RelationalParameter(DocumentIdParameterName, documentId)]
            ),
            SqlDialect.Mssql => new RelationalCommand(
                $"""
                SELECT
                    document.{contentVersionColumn} AS {contentVersionColumn}
                FROM {quotedTable} document WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
                WHERE document.{documentIdColumn} = @documentId
                """,
                [new RelationalParameter(DocumentIdParameterName, documentId)]
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
        };
    }
}
