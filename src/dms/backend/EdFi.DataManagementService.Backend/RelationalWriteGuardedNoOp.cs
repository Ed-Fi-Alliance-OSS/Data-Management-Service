// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend;

internal static class RelationalWriteGuardedNoOp
{
    public static bool IsNoOpCandidate(RelationalWriteMergeResult mergeResult)
    {
        ArgumentNullException.ThrowIfNull(mergeResult);

        foreach (var tableState in mergeResult.TablesInDependencyOrder)
        {
            if (tableState.CurrentRows.Length != tableState.MergedRows.Length)
            {
                return false;
            }

            for (var rowIndex = 0; rowIndex < tableState.CurrentRows.Length; rowIndex++)
            {
                if (
                    !tableState
                        .CurrentRows[rowIndex]
                        .ComparableValues.SequenceEqual(tableState.MergedRows[rowIndex].ComparableValues)
                )
                {
                    return false;
                }
            }
        }

        return true;
    }
}

/// <summary>
/// Shared builder for the <c>dms.Document</c> row-lock command used by both the
/// <c>If-Match</c> changed-write pre-check and the guarded no-op freshness checker.
/// Centralising the SQL prevents the two callers from drifting across dialects.
/// </summary>
internal static class DocumentRowLock
{
    internal const string DocumentIdParameterName = "@documentId";

    internal static RelationalCommand BuildCommand(SqlDialect dialect, long documentId) =>
        dialect switch
        {
            SqlDialect.Pgsql => new RelationalCommand(
                """
                SELECT
                    document."ContentVersion" AS "ContentVersion"
                FROM dms."Document" document
                WHERE document."DocumentId" = @documentId
                FOR UPDATE
                """,
                [new RelationalParameter(DocumentIdParameterName, documentId)]
            ),
            SqlDialect.Mssql => new RelationalCommand(
                """
                SELECT
                    document.[ContentVersion] AS [ContentVersion]
                FROM [dms].[Document] document WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
                WHERE document.[DocumentId] = @documentId
                """,
                [new RelationalParameter(DocumentIdParameterName, documentId)]
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
        };
}

internal interface IRelationalWriteFreshnessChecker
{
    Task<bool> IsCurrentAsync(
        RelationalWriteExecutorRequest request,
        RelationalWriteTargetContext.ExistingDocument targetContext,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken = default
    );
}

internal sealed class RelationalWriteFreshnessChecker : IRelationalWriteFreshnessChecker
{
    public async Task<bool> IsCurrentAsync(
        RelationalWriteExecutorRequest request,
        RelationalWriteTargetContext.ExistingDocument targetContext,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(targetContext);
        ArgumentNullException.ThrowIfNull(writeSession);

        await using var command = writeSession.CreateCommand(
            DocumentRowLock.BuildCommand(request.MappingSet.Key.Dialect, targetContext.DocumentId)
        );

        var scalarResult = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        if (scalarResult is null or DBNull)
        {
            return false;
        }

        var currentContentVersion = Convert.ToInt64(scalarResult, CultureInfo.InvariantCulture);

        return currentContentVersion == targetContext.ObservedContentVersion;
    }
}
