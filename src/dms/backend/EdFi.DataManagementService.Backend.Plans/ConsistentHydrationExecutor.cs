// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Hydrates standalone reads with a document-version fence after all representation queries.
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
    /// batch, with one authoritative timestamp SELECT and one final document-only version SELECT.
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

        string sql =
            HydrationBatchBuilder.Build(plan, keyset, dialect, executionOptions)
            + BuildVersionFence(plan.KeysetTable, keyset, dialect, executionOptions);

        for (int attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            await using DbCommand command = createCommand(sql);
            HydrationBatchBuilder.AddParameters(command, keyset);
            await using DbDataReader reader = await command.ExecuteReaderAsync(ct);
            HydratedPage page = await HydrationExecutor.ReadPageAsync(
                reader,
                plan,
                keyset,
                executionOptions,
                ct
            );
            if (!await reader.NextResultAsync(ct))
            {
                throw new InvalidOperationException(
                    "Expected the hydration document-version fence result set."
                );
            }
            Dictionary<long, long> versions = [];
            while (await reader.ReadAsync(ct))
            {
                versions.Add(reader.GetInt64(0), reader.GetInt64(1));
            }

            // Every representation-affecting write, including child changes and restamps, advances
            // Document.ContentVersion transactionally with the authoritative owner stamp. Equality
            // across the entire acquisition proves that metadata and content belong together. A
            // disappeared document is also retried; extra ids without initial metadata are not served.
            if (
                page.DocumentMetadata.All(metadata =>
                    versions.TryGetValue(metadata.DocumentId, out long version)
                    && version == metadata.ContentVersion
                )
            )
            {
                return page;
            }
        }

        throw new InvalidOperationException(
            "Relational hydration could not read a stable representation after concurrent writes."
        );
    }

    private static string BuildVersionFence(
        KeysetTableContract keysetTable,
        PageKeysetSpec keyset,
        SqlDialect dialect,
        HydrationExecutionOptions executionOptions
    )
    {
        SqlWriter writer = new(SqlDialectFactory.Create(dialect));
        string id = writer.Dialect.QuoteIdentifier("DocumentId");
        string version = writer.Dialect.QuoteIdentifier("ContentVersion");
        writer
            .AppendLine()
            .Append($"SELECT d.{id}, d.{version} FROM ")
            .AppendTable(new(new("dms"), "Document"))
            .Append(" d");
        if (
            keyset is PageKeysetSpec.Single
            && executionOptions.UseSingleDocumentFastPath
            && PlanSqlDialectFactory.Create(dialect).SupportsSingleDocumentHydration
        )
        {
            writer
                .Append($" WHERE d.{id} = ")
                .AppendParameter(HydrationSqlConventions.SingleDocumentIdParameterName);
        }
        else
        {
            writer
                .Append(" INNER JOIN ")
                .AppendRelation(keysetTable.Table)
                .Append($" k ON d.{id} = k.")
                .AppendQuoted(keysetTable.DocumentIdColumnName.Value);
        }
        return writer.AppendLine(";").ToString();
    }
}
