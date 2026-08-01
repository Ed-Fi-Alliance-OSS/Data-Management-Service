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
    /// <b>Known ordering hazard.</b> Locking the root row inverts this writer's lock order relative to
    /// the stamping/propagation cascades: the writer now takes <c>Root(D)</c> first and only reaches
    /// <c>dms.Document(D)</c> later, through the stamping trigger during persist, while a cascade
    /// triggered by another transaction still takes <c>dms.Document(D)</c> before <c>Root(D)</c>. Under
    /// that narrow contention pattern the two orders can deadlock, which was impossible while both
    /// sides locked <c>dms.Document</c> first. The resilience pipeline already absorbs it — deadlock and
    /// serialization failures (SQL Server 1205, PostgreSQL 40P01) classify as transient and replay the
    /// whole write — and Phase 4 dissolves it outright by removing the <c>dms.Document</c> write, after
    /// which the root row is the only row either side takes.
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
