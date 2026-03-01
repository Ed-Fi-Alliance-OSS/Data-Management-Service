// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

internal enum ThinSliceWritePlanUnsupportedReason
{
    None,
    NonRelationalStorage,
    NonRootOnly,
    RootHasKeyUnificationClasses,
    RootHasStoredNonKeyColumnsWithoutSourceJsonPath,
}

internal readonly record struct ThinSliceWritePlanSupportResult(
    ThinSliceWritePlanUnsupportedReason UnsupportedReason,
    ResourceStorageKind StorageKind,
    int TableCount,
    int RootKeyUnificationClassCount,
    int RootStoredNonKeyColumnsWithoutSourceJsonPathCount
)
{
    public bool IsSupported => UnsupportedReason == ThinSliceWritePlanUnsupportedReason.None;
}

internal static class ThinSliceWritePlanSupportEvaluator
{
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
