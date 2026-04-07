// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Executes compiled <see cref="DescriptorProjectionPlan"/> SQL within a write session,
/// returning a <c>DescriptorId → Uri</c> lookup map.
/// </summary>
public static class DescriptorProjectionExecutor
{
    /// <summary>
    /// Executes each plan's <c>SelectByKeysetSql</c> against the supplied connection and transaction,
    /// accumulating all <c>DescriptorId → Uri</c> pairs into a single lookup dictionary.
    /// </summary>
    /// <param name="connection">An already-opened database connection.</param>
    /// <param name="transaction">The active write-session transaction bound to the connection.</param>
    /// <param name="plans">Compiled descriptor projection plans to execute.</param>
    /// <param name="keyset">The page keyset specification used to parameterize each query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A read-only dictionary mapping each <c>DescriptorId</c> to its canonical URI string.
    /// Returns an empty dictionary when <paramref name="plans"/> is empty.
    /// </returns>
    public static async Task<IReadOnlyDictionary<long, string>> ExecuteAsync(
        DbConnection connection,
        DbTransaction transaction,
        IReadOnlyList<DescriptorProjectionPlan> plans,
        PageKeysetSpec keyset,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(keyset);

        if (plans.Count == 0)
        {
            return new Dictionary<long, string>();
        }

        Dictionary<long, string> lookup = [];

        foreach (var plan in plans)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = plan.SelectByKeysetSql;
            HydrationBatchBuilder.AddParameters(command, keyset);

            await using var reader = await command.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                var descriptorId = reader.GetInt64(plan.ResultShape.DescriptorIdOrdinal);
                var uri = reader.GetString(plan.ResultShape.UriOrdinal);
                lookup.TryAdd(descriptorId, uri);
            }
        }

        return lookup;
    }

    /// <summary>
    /// Builds a <c>DescriptorId → Uri</c> lookup from pre-resolved pairs.
    /// </summary>
    /// <remarks>
    /// This overload exists for unit-test use where the caller already holds the resolved pairs
    /// and only needs the lookup assembled — no database access is required.
    /// </remarks>
    /// <param name="pairs">Pre-resolved <c>(DescriptorId, Uri)</c> pairs to index.</param>
    /// <returns>
    /// A read-only dictionary mapping each <c>DescriptorId</c> to its URI.
    /// Returns an empty dictionary when <paramref name="pairs"/> is empty.
    /// </returns>
    public static IReadOnlyDictionary<long, string> BuildLookupFromPairs(
        IReadOnlyList<(long DescriptorId, string Uri)> pairs
    )
    {
        ArgumentNullException.ThrowIfNull(pairs);

        if (pairs.Count == 0)
        {
            return new Dictionary<long, string>();
        }

        Dictionary<long, string> lookup = new(pairs.Count);

        foreach (var (descriptorId, uri) in pairs)
        {
            lookup.TryAdd(descriptorId, uri);
        }

        return lookup;
    }
}
