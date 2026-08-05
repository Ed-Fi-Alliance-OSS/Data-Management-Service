// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Globalization;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Reserves the collection keys no statement can produce inline, in one command for every table that needs
/// one.
/// </summary>
/// <remarks>
/// A key is reserved only when some table other than its owner binds it, because a statement cannot read a
/// value the sequence produced inside another statement's row. One shared command serves them all: reserving
/// per table would reintroduce the per-table round trip this batching exists to remove.
/// </remarks>
internal static class RelationalWriteCollectionItemIdReservation
{
    public static async Task ReserveAsync(
        SqlDialect dialect,
        IReadOnlyList<FlattenedWriteValue.UnresolvedCollectionItemId> collectionItemIds,
        RelationalWriteCollectionItemIdBindings collectionItemIdBindings,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(collectionItemIds);
        ArgumentNullException.ThrowIfNull(collectionItemIdBindings);
        ArgumentNullException.ThrowIfNull(writeSession);

        List<FlattenedWriteValue.UnresolvedCollectionItemId> missing = new(collectionItemIds.Count);

        foreach (var collectionItemId in collectionItemIds)
        {
            if (
                !collectionItemIdBindings.HasReservedValue(collectionItemId)
                && !collectionItemIdBindings.IsInlined(collectionItemId)
            )
            {
                missing.Add(collectionItemId);
            }
        }

        if (missing.Count == 0)
        {
            return;
        }

        if (missing.Count == 1)
        {
            await using var singleCommand = writeSession.CreateCommand(BuildSingleCommand(dialect));
            var scalarResult = await singleCommand
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false);

            if (scalarResult is null or DBNull)
            {
                throw new InvalidOperationException(
                    "CollectionItemId reservation did not return a value from dms.CollectionItemIdSequence."
                );
            }

            collectionItemIdBindings.AddReservedValue(
                missing[0],
                Convert.ToInt64(scalarResult, CultureInfo.InvariantCulture)
            );

            return;
        }

        await using var command = writeSession.CreateCommand(BuildBatchCommand(dialect, missing.Count));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var reservedValuesInOrder = await ReadReservedValuesAsync(reader, missing.Count, cancellationToken)
            .ConfigureAwait(false);

        for (var index = 0; index < missing.Count; index++)
        {
            collectionItemIdBindings.AddReservedValue(missing[index], reservedValuesInOrder[index]);
        }
    }

    private static RelationalCommand BuildSingleCommand(SqlDialect dialect) =>
        dialect switch
        {
            SqlDialect.Pgsql => new RelationalCommand(
                """
                SELECT nextval('"dms"."CollectionItemIdSequence"');
                """,
                []
            ),
            SqlDialect.Mssql => new RelationalCommand(
                """
                SELECT NEXT VALUE FOR [dms].[CollectionItemIdSequence];
                """,
                []
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
        };

    private static RelationalCommand BuildBatchCommand(SqlDialect dialect, int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        return dialect switch
        {
            SqlDialect.Pgsql => new RelationalCommand(
                """
                SELECT
                    series."Ordinal" AS "Ordinal",
                    nextval('"dms"."CollectionItemIdSequence"') AS "CollectionItemId"
                FROM generate_series(1, @count) AS series("Ordinal");
                """,
                [new RelationalParameter("@count", count)]
            ),
            SqlDialect.Mssql => new RelationalCommand(
                """
                WITH [sequence_request] ([Ordinal]) AS (
                    SELECT 1
                    UNION ALL
                    SELECT [Ordinal] + 1
                    FROM [sequence_request]
                    WHERE [Ordinal] < @count
                )
                SELECT
                    [sequence_request].[Ordinal] AS [Ordinal],
                    NEXT VALUE FOR [dms].[CollectionItemIdSequence] OVER (ORDER BY [sequence_request].[Ordinal]) AS [CollectionItemId]
                FROM [sequence_request]
                OPTION (MAXRECURSION 0);
                """,
                [new RelationalParameter("@count", count)]
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
        };
    }

    private static async Task<long[]> ReadReservedValuesAsync(
        DbDataReader reader,
        int expectedCount,
        CancellationToken cancellationToken
    )
    {
        var ordinalColumnOrdinal = reader.GetOrdinal("Ordinal");
        var collectionItemIdColumnOrdinal = reader.GetOrdinal("CollectionItemId");
        var reservedCollectionItemIds = new long[expectedCount];
        var assignedOrdinals = new bool[expectedCount];
        var rowCount = 0;

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var ordinal = await reader
                .GetFieldValueAsync<int>(ordinalColumnOrdinal, cancellationToken)
                .ConfigureAwait(false);

            if (ordinal < 1 || ordinal > expectedCount)
            {
                throw new InvalidOperationException(
                    "CollectionItemId reservation returned an out-of-range ordinal value "
                        + $"({ordinal}) for batch size {expectedCount}."
                );
            }

            var index = ordinal - 1;

            if (assignedOrdinals[index])
            {
                throw new InvalidOperationException(
                    $"CollectionItemId reservation returned duplicate ordinal value {ordinal}."
                );
            }

            reservedCollectionItemIds[index] = await reader
                .GetFieldValueAsync<long>(collectionItemIdColumnOrdinal, cancellationToken)
                .ConfigureAwait(false);
            assignedOrdinals[index] = true;
            rowCount++;
        }

        if (rowCount != expectedCount || Array.Exists(assignedOrdinals, static assigned => !assigned))
        {
            throw new InvalidOperationException(
                $"CollectionItemId reservation returned {rowCount} rows for requested batch size {expectedCount}."
            );
        }

        return reservedCollectionItemIds;
    }
}
