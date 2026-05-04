// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Equality comparer for <see cref="object"/>?[] arrays that compares element-by-element using
/// the runtime <see cref="object.Equals(object?, object?)"/> contract. Used to key dictionaries
/// and hash sets by raw semantic identity values without distinguishing missing identity
/// properties from explicit JSON nulls — both collapse to a <c>null</c> array element and
/// therefore compare equal. The shared duplicate-detection step in
/// <c>RelationalWriteFlattener.MaterializeCollectionCandidates</c> and the row-match step in
/// <c>RelationalWriteNoProfileMerge.ProjectedCollectionTableState</c> must agree on this
/// collapsing rule, so they intentionally share a single comparer instance.
/// </summary>
internal sealed class ObjectValueArrayComparer : IEqualityComparer<object?[]>
{
    public static ObjectValueArrayComparer Instance { get; } = new();

    public bool Equals(object?[]? left, object?[]? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Length != right.Length)
        {
            return false;
        }

        for (var index = 0; index < left.Length; index++)
        {
            if (!Equals(left[index], right[index]))
            {
                return false;
            }
        }

        return true;
    }

    public int GetHashCode(object?[] values)
    {
        ArgumentNullException.ThrowIfNull(values);

        HashCode hashCode = new();
        hashCode.Add(values.Length);

        foreach (var value in values)
        {
            hashCode.Add(value);
        }

        return hashCode.ToHashCode();
    }
}
