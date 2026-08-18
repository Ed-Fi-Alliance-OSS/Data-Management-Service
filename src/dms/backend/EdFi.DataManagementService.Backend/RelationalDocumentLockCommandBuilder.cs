// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend;

internal static class RelationalDocumentLockCommandBuilder
{
    private const string DocumentIdParameterName = "@documentId";
    private const string EnqueueOutcomeNoWorkQueuedParameterName = "@enqueueOutcomeNoWorkQueued";
    private const string EnqueueOutcomeAlreadySatisfiedParameterName = "@enqueueOutcomeAlreadySatisfied";

    public static RelationalCommand BuildContentVersionCommand(SqlDialect dialect, long documentId) =>
        new(BuildContentVersionSql(dialect), [new RelationalParameter(DocumentIdParameterName, documentId)]);

    public static RelationalCommand BuildContentVersionWithDocumentCacheEnqueueOutcomeCommand(
        SqlDialect dialect,
        long documentId
    ) =>
        new(
            BuildContentVersionWithDocumentCacheEnqueueOutcomeSql(dialect),
            [
                new RelationalParameter(DocumentIdParameterName, documentId),
                new RelationalParameter(
                    EnqueueOutcomeNoWorkQueuedParameterName,
                    (int)DocumentCacheEnqueueOutcome.NoWorkQueued
                ),
                new RelationalParameter(
                    EnqueueOutcomeAlreadySatisfiedParameterName,
                    (int)DocumentCacheEnqueueOutcome.AlreadySatisfied
                ),
            ]
        );

    /// <summary>
    /// The same statement with the document id supplied as a SQL expression instead of a bound value, for a
    /// create whose identity an earlier statement of the same command generated.
    /// </summary>
    public static RelationalCommand BuildContentVersionCommand(SqlDialect dialect, string documentIdSql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentIdSql);

        return new RelationalCommand(
            RelationalParameterTokenRewriter.Rewrite(
                BuildContentVersionSql(dialect),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [RelationalParameterTokenRewriter.BareName(DocumentIdParameterName)] = documentIdSql,
                }
            ),
            []
        );
    }

    /// <summary>
    /// The same statement with the document id supplied as a SQL expression instead of a bound value, for a
    /// create whose identity an earlier statement of the same command generated.
    /// </summary>
    public static RelationalCommand BuildContentVersionWithDocumentCacheEnqueueOutcomeCommand(
        SqlDialect dialect,
        string documentIdSql
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentIdSql);

        return new RelationalCommand(
            RelationalParameterTokenRewriter.Rewrite(
                BuildContentVersionWithDocumentCacheEnqueueOutcomeSql(dialect),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [RelationalParameterTokenRewriter.BareName(DocumentIdParameterName)] = documentIdSql,
                }
            ),
            [
                new RelationalParameter(
                    EnqueueOutcomeNoWorkQueuedParameterName,
                    (int)DocumentCacheEnqueueOutcome.NoWorkQueued
                ),
                new RelationalParameter(
                    EnqueueOutcomeAlreadySatisfiedParameterName,
                    (int)DocumentCacheEnqueueOutcome.AlreadySatisfied
                ),
            ]
        );
    }

    private static string BuildContentVersionSql(SqlDialect dialect)
    {
        return dialect switch
        {
            SqlDialect.Pgsql => """
                SELECT
                    document."ContentVersion" AS "ContentVersion"
                FROM dms."Document" document
                WHERE document."DocumentId" = @documentId
                FOR UPDATE
                """,
            SqlDialect.Mssql => """
                SELECT
                    document.[ContentVersion] AS [ContentVersion]
                FROM [dms].[Document] document WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
                WHERE document.[DocumentId] = @documentId
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
        };
    }

    private static string BuildContentVersionWithDocumentCacheEnqueueOutcomeSql(SqlDialect dialect)
    {
        return dialect switch
        {
            SqlDialect.Pgsql => """
                SELECT
                    document."ContentVersion" AS "ContentVersion",
                    CASE
                        WHEN EXISTS (
                            SELECT 1
                            FROM dms."DocumentProjectionWork" work
                            WHERE work."DocumentId" = document."DocumentId"
                              AND work."RequiredContentVersion" >= document."ContentVersion"
                        )
                        THEN @enqueueOutcomeAlreadySatisfied
                        ELSE @enqueueOutcomeNoWorkQueued
                    END AS "DocumentCacheEnqueueOutcome"
                FROM dms."Document" document
                WHERE document."DocumentId" = @documentId
                FOR UPDATE
                """,
            SqlDialect.Mssql => """
                SELECT
                    document.[ContentVersion] AS [ContentVersion],
                    CAST(CASE
                        WHEN EXISTS (
                            SELECT TOP (1) 1
                            FROM [dms].[DocumentProjectionWork] work
                            WHERE work.[DocumentId] = document.[DocumentId]
                              AND work.[RequiredContentVersion] >= document.[ContentVersion]
                        )
                        THEN @enqueueOutcomeAlreadySatisfied
                        ELSE @enqueueOutcomeNoWorkQueued
                    END AS int) AS [DocumentCacheEnqueueOutcome]
                FROM [dms].[Document] document WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
                WHERE document.[DocumentId] = @documentId
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
        };
    }
}
