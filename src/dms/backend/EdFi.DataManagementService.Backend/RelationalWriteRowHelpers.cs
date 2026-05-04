// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Pure row-shaping helpers shared between <see cref="RelationalWriteNoProfileMergeSynthesizer"/>
/// and the profile collection merge path. Extracted to avoid duplication as profile
/// collection merge adopts the same row-shaping primitives that no-profile already uses.
/// </summary>
internal static class RelationalWriteRowHelpers
{
    public static ImmutableArray<FlattenedWriteValue> RewriteParentKeyPartValues(
        TableWritePlan tableWritePlan,
        IReadOnlyList<FlattenedWriteValue> values,
        IReadOnlyList<FlattenedWriteValue> parentPhysicalRowIdentityValues
    )
    {
        FlattenedWriteValue[] rewrittenValues = [.. values];

        for (var bindingIndex = 0; bindingIndex < tableWritePlan.ColumnBindings.Length; bindingIndex++)
        {
            if (
                tableWritePlan.ColumnBindings[bindingIndex].Source
                is not WriteValueSource.ParentKeyPart parentKeyPart
            )
            {
                continue;
            }

            rewrittenValues[bindingIndex] = parentPhysicalRowIdentityValues[parentKeyPart.Index];
        }

        return rewrittenValues.ToImmutableArray();
    }

    public static ImmutableArray<FlattenedWriteValue> RewriteCollectionStableRowIdentity(
        TableWritePlan tableWritePlan,
        IReadOnlyList<FlattenedWriteValue> values,
        IReadOnlyList<FlattenedWriteValue> matchedCurrentRowValues
    )
    {
        var mergePlan =
            tableWritePlan.CollectionMergePlan
            ?? throw new InvalidOperationException(
                $"Collection table '{RelationalWriteFlattener.FormatTable(tableWritePlan)}' does not have a compiled collection merge plan."
            );

        FlattenedWriteValue[] rewrittenValues = [.. values];
        rewrittenValues[mergePlan.StableRowIdentityBindingIndex] = matchedCurrentRowValues[
            mergePlan.StableRowIdentityBindingIndex
        ];

        return rewrittenValues.ToImmutableArray();
    }

    public static RelationalWriteMergedTableRow CreateMergedTableRow(
        TableWritePlan tableWritePlan,
        IReadOnlyList<FlattenedWriteValue> values
    )
    {
        var comparableValues = RelationalWriteMergeSupport.ProjectComparableValues(tableWritePlan, values);

        return new RelationalWriteMergedTableRow(values, comparableValues);
    }
}
