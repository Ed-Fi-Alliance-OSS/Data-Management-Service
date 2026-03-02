// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Classifies why a resource is not eligible for thin-slice root-only write plan compilation.
/// </summary>
internal enum ThinSliceWritePlanUnsupportedReason
{
    /// <summary>
    /// The resource is supported.
    /// </summary>
    None,

    /// <summary>
    /// The resource is not stored as per-resource relational tables.
    /// </summary>
    NonRelationalStorage,

    /// <summary>
    /// The resource has child/extension tables (not root-only).
    /// </summary>
    NonRootOnly,

    /// <summary>
    /// The root table has key-unification inventory, which requires precompute support not included in the thin slice.
    /// </summary>
    RootHasKeyUnificationClasses,

    /// <summary>
    /// The root table has stored non-key columns without a source JSON path, indicating precomputed values are required.
    /// </summary>
    RootHasStoredNonKeyColumnsWithoutSourceJsonPath,
}

/// <summary>
/// Thin-slice eligibility and diagnostic details for root-only write plan compilation.
/// </summary>
/// <param name="UnsupportedReason">The reason the resource is unsupported, or <see cref="ThinSliceWritePlanUnsupportedReason.None" /> when supported.</param>
/// <param name="StorageKind">The resource storage kind.</param>
/// <param name="TableCount">The number of tables in <c>TablesInDependencyOrder</c>.</param>
/// <param name="RootKeyUnificationClassCount">The number of key-unification classes on the root table.</param>
/// <param name="RootStoredNonKeyColumnsWithoutSourceJsonPathCount">
/// Count of stored non-key columns on the root table that lack a source JSON path.
/// </param>
internal readonly record struct ThinSliceWritePlanSupportResult(
    ThinSliceWritePlanUnsupportedReason UnsupportedReason,
    ResourceStorageKind StorageKind,
    int TableCount,
    int RootKeyUnificationClassCount,
    int RootStoredNonKeyColumnsWithoutSourceJsonPathCount
)
{
    /// <summary>
    /// Gets a value indicating whether the resource is supported by thin-slice write plan compilation.
    /// </summary>
    public bool IsSupported => UnsupportedReason == ThinSliceWritePlanUnsupportedReason.None;
}

/// <summary>
/// Evaluates whether a resource model is eligible for thin-slice root-only relational-table write plan compilation.
/// </summary>
internal static class ThinSliceWritePlanSupportEvaluator
{
    /// <summary>
    /// Evaluates thin-slice write plan support and returns a deterministic diagnostic result.
    /// </summary>
    /// <param name="resourceModel">The resource model to evaluate.</param>
    /// <returns>Support result describing eligibility and any unsupported reason.</returns>
    public static ThinSliceWritePlanSupportResult Evaluate(RelationalResourceModel resourceModel)
    {
        ArgumentNullException.ThrowIfNull(resourceModel);

        var rootTable = resourceModel.Root;
        var rootKeyUnificationClassCount = rootTable.KeyUnificationClasses.Count;
        var rootStoredNonKeyColumnsWithoutSourceJsonPathCount = CountStoredNonKeyColumnsWithoutSourceJsonPath(
            rootTable
        );

        var unsupportedReason = resourceModel switch
        {
            { StorageKind: not ResourceStorageKind.RelationalTables } =>
                ThinSliceWritePlanUnsupportedReason.NonRelationalStorage,
            { TablesInDependencyOrder.Count: not 1 } => ThinSliceWritePlanUnsupportedReason.NonRootOnly,
            _ when rootKeyUnificationClassCount > 0 =>
                ThinSliceWritePlanUnsupportedReason.RootHasKeyUnificationClasses,
            _ when rootStoredNonKeyColumnsWithoutSourceJsonPathCount > 0 =>
                ThinSliceWritePlanUnsupportedReason.RootHasStoredNonKeyColumnsWithoutSourceJsonPath,
            _ => ThinSliceWritePlanUnsupportedReason.None,
        };

        return new ThinSliceWritePlanSupportResult(
            UnsupportedReason: unsupportedReason,
            StorageKind: resourceModel.StorageKind,
            TableCount: resourceModel.TablesInDependencyOrder.Count,
            RootKeyUnificationClassCount: rootKeyUnificationClassCount,
            RootStoredNonKeyColumnsWithoutSourceJsonPathCount: rootStoredNonKeyColumnsWithoutSourceJsonPathCount
        );
    }

    /// <summary>
    /// Counts stored non-key columns that have no source JSON path, which require precomputed support (for example key unification).
    /// </summary>
    private static int CountStoredNonKeyColumnsWithoutSourceJsonPath(DbTableModel rootTable)
    {
        var keyColumns = rootTable.Key.Columns.Select(static keyColumn => keyColumn.ColumnName).ToHashSet();

        return rootTable.Columns.Count(column =>
            column.Storage is ColumnStorage.Stored
            && !keyColumns.Contains(column.ColumnName)
            && column.SourceJsonPath is null
        );
    }
}
