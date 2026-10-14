// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Hydrates standalone reads with document-version fences before metadata and after all representation queries.
/// Session-scoped writes continue to use <see cref="HydrationExecutor"/> under their own transaction.
/// </summary>
public static class ConsistentHydrationExecutor
{
    private const int MaximumAttempts = 3;

    /// <summary>
    /// Reads a coherent representation using the caller's open connection, without requiring
    /// snapshot isolation or retaining read locks while a concurrent writer commits.
    /// </summary>
    public static Task<HydratedPage> ExecuteAsync(
        DbConnection connection,
        ResourceReadPlan plan,
        PageKeysetSpec keyset,
        SqlDialect dialect,
        HydrationExecutionOptions executionOptions,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(connection);
        return ExecuteAsync(
            sql =>
            {
                DbCommand command = connection.CreateCommand();
                command.CommandText = sql;
                return command;
            },
            plan,
            keyset,
            dialect,
            executionOptions,
            ct
        );
    }

    /// <summary>
    /// Executes through the same command-creation seam as ordinary hydration. Each attempt is one
    /// batch, with one authoritative timestamp SELECT bracketed by document-only version SELECTs.
    /// </summary>
    public static async Task<HydratedPage> ExecuteAsync(
        Func<string, DbCommand> createCommand,
        ResourceReadPlan plan,
        PageKeysetSpec keyset,
        SqlDialect dialect,
        HydrationExecutionOptions executionOptions,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(createCommand);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(keyset);
        ArgumentNullException.ThrowIfNull(executionOptions);

        string sql = HydrationBatchBuilder.BuildConsistent(plan, keyset, dialect, executionOptions);

        for (int attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            await using DbCommand command = createCommand(sql);
            HydrationBatchBuilder.AddParameters(command, keyset);
            await using DbDataReader reader = await command.ExecuteReaderAsync(ct);
            Dictionary<long, long> leadingVersions = [];
            HydratedPage page = await HydrationExecutor.ReadPageAsync(
                reader,
                plan,
                keyset,
                executionOptions,
                async (leadingReader, token) =>
                {
                    leadingVersions = await ReadVersionsAsync(leadingReader, token);
                    if (!await leadingReader.NextResultAsync(token))
                    {
                        throw new InvalidOperationException(
                            "Expected document metadata after the leading hydration document-version fence result set."
                        );
                    }
                },
                ct
            );
            if (!await reader.NextResultAsync(ct))
            {
                throw new InvalidOperationException(
                    "Expected the hydration document-version fence result set."
                );
            }
            Dictionary<long, long> versions = await ReadVersionsAsync(reader, ct);

            // Every representation-affecting write, including child changes and restamps, advances
            // Document.ContentVersion transactionally with the authoritative owner stamp. Equality
            // across both boundaries and the joined metadata proves that stamps and content belong
            // together, even when locking READ COMMITTED observes different commits within the join. A
            // disappeared document is also retried; extra ids without initial metadata are not served.
            if (
                page.DocumentMetadata.All(metadata =>
                    leadingVersions.TryGetValue(metadata.DocumentId, out long leadingVersion)
                    && versions.TryGetValue(metadata.DocumentId, out long version)
                    && leadingVersion == metadata.ContentVersion
                    && version == metadata.ContentVersion
                )
            )
            {
                return page;
            }
        }

        throw new HydrationConsistencyException();
    }

    private static async Task<Dictionary<long, long>> ReadVersionsAsync(
        DbDataReader reader,
        CancellationToken ct
    )
    {
        Dictionary<long, long> versions = [];
        while (await reader.ReadAsync(ct))
        {
            versions.Add(reader.GetInt64(0), reader.GetInt64(1));
        }
        return versions;
    }
}
