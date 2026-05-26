// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend;

internal sealed record RelationalWriteMergedTableRow
{
    public RelationalWriteMergedTableRow(
        IEnumerable<FlattenedWriteValue> values,
        IEnumerable<FlattenedWriteValue> comparableValues
    )
    {
        Values = FlattenedWriteContractSupport.ToImmutableArray(values, nameof(values));
        ComparableValues = FlattenedWriteContractSupport.ToImmutableArray(
            comparableValues,
            nameof(comparableValues)
        );
    }

    public ImmutableArray<FlattenedWriteValue> Values { get; init; }

    public ImmutableArray<FlattenedWriteValue> ComparableValues { get; init; }
}

internal sealed record RelationalWriteMergedTableState
{
    public RelationalWriteMergedTableState(
        TableWritePlan tableWritePlan,
        IEnumerable<RelationalWriteMergedTableRow> currentRows,
        IEnumerable<RelationalWriteMergedTableRow> mergedRows
    )
    {
        TableWritePlan = tableWritePlan ?? throw new ArgumentNullException(nameof(tableWritePlan));
        CurrentRows = FlattenedWriteContractSupport.ToImmutableArray(currentRows, nameof(currentRows));
        MergedRows = FlattenedWriteContractSupport.ToImmutableArray(mergedRows, nameof(mergedRows));
    }

    public TableWritePlan TableWritePlan { get; init; }

    public ImmutableArray<RelationalWriteMergedTableRow> CurrentRows { get; init; }

    public ImmutableArray<RelationalWriteMergedTableRow> MergedRows { get; init; }
}

internal sealed record RelationalWriteMergeResult
{
    public RelationalWriteMergeResult(
        IEnumerable<RelationalWriteMergedTableState> tablesInDependencyOrder,
        bool supportsGuardedNoOp
    )
    {
        TablesInDependencyOrder = FlattenedWriteContractSupport.ToImmutableArray(
            tablesInDependencyOrder,
            nameof(tablesInDependencyOrder)
        );
        SupportsGuardedNoOp = supportsGuardedNoOp;
    }

    public ImmutableArray<RelationalWriteMergedTableState> TablesInDependencyOrder { get; init; }

    public bool SupportsGuardedNoOp { get; init; }
}
