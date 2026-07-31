// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Decides, for one request's merged rows, how each unresolved <c>CollectionItemId</c> becomes a value:
/// an inline sequence expression the inserting statement evaluates server-side, or a client-side
/// reservation the caller must hold because another table's statement binds the same value.
/// </summary>
/// <remarks>
/// <para>
/// A token is inlined when it occurs exactly once across every table's merged rows and that occurrence
/// is the owning table's own preallocated collection key. Both conditions are needed: the sequence is
/// evaluated per row, so two occurrences of one token would receive two different values, and a token
/// appearing anywhere other than its owner's key column is a reference to a row some other statement
/// inserts.
/// </para>
/// <para>
/// Inlining is what removes the per-table reservation round trip. The reservation shapes are retained
/// for the tokens that genuinely cannot be inlined — a collection-aligned extension scope's rows carry
/// the parent collection row's id, and a separate statement cannot read a value the sequence produced
/// inside another statement's row.
/// </para>
/// </remarks>
internal sealed class RelationalWriteCollectionItemIdBindings
{
    private readonly Dictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> _reservedValues = [];

    private readonly HashSet<FlattenedWriteValue.UnresolvedCollectionItemId> _inlinedTokens;

    private RelationalWriteCollectionItemIdBindings(
        HashSet<FlattenedWriteValue.UnresolvedCollectionItemId> inlinedTokens,
        string sequenceExpression
    )
    {
        _inlinedTokens = inlinedTokens;
        SequenceExpression = sequenceExpression;
    }

    /// <summary>The provider expression that yields the next collection key, evaluated once per row.</summary>
    public string SequenceExpression { get; }

    public static RelationalWriteCollectionItemIdBindings Create(
        SqlDialect dialect,
        RelationalWriteMergeResult mergeResult
    )
    {
        ArgumentNullException.ThrowIfNull(mergeResult);

        Dictionary<FlattenedWriteValue.UnresolvedCollectionItemId, int> occurrenceCounts = [];
        HashSet<FlattenedWriteValue.UnresolvedCollectionItemId> ownedKeyTokens = [];

        var mergedRowsWithOwnedKeyIndex = mergeResult.TablesInDependencyOrder.SelectMany(tableState =>
            tableState.MergedRows.Select(mergedRow =>
                (
                    mergedRow.Values,
                    OwnedKeyBindingIndex: tableState
                        .TableWritePlan
                        .CollectionKeyPreallocationPlan
                        ?.BindingIndex
                )
            )
        );

        foreach (var (values, ownedKeyBindingIndex) in mergedRowsWithOwnedKeyIndex)
        {
            for (var bindingIndex = 0; bindingIndex < values.Length; bindingIndex++)
            {
                if (values[bindingIndex] is not FlattenedWriteValue.UnresolvedCollectionItemId token)
                {
                    continue;
                }

                occurrenceCounts[token] = occurrenceCounts.GetValueOrDefault(token) + 1;

                if (bindingIndex == ownedKeyBindingIndex)
                {
                    ownedKeyTokens.Add(token);
                }
            }
        }

        HashSet<FlattenedWriteValue.UnresolvedCollectionItemId> inlinedTokens = [];

        foreach (var token in ownedKeyTokens)
        {
            if (occurrenceCounts[token] == 1)
            {
                inlinedTokens.Add(token);
            }
        }

        return new RelationalWriteCollectionItemIdBindings(inlinedTokens, BuildSequenceExpression(dialect));
    }

    /// <summary>
    /// True when the inserting statement produces this token's value itself, so it neither needs a
    /// reservation nor binds a parameter.
    /// </summary>
    public bool IsInlined(FlattenedWriteValue.UnresolvedCollectionItemId token) =>
        _inlinedTokens.Contains(token);

    public bool TryGetReservedValue(
        FlattenedWriteValue.UnresolvedCollectionItemId token,
        out long reservedValue
    ) => _reservedValues.TryGetValue(token, out reservedValue);

    public bool HasReservedValue(FlattenedWriteValue.UnresolvedCollectionItemId token) =>
        _reservedValues.ContainsKey(token);

    public void AddReservedValue(FlattenedWriteValue.UnresolvedCollectionItemId token, long reservedValue) =>
        _reservedValues.Add(token, reservedValue);

    private static string BuildSequenceExpression(SqlDialect dialect) =>
        dialect switch
        {
            SqlDialect.Pgsql => """nextval('"dms"."CollectionItemIdSequence"')""",
            SqlDialect.Mssql => "NEXT VALUE FOR [dms].[CollectionItemIdSequence]",
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
        };
}
