// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Plans;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Splits a planned custom-view check list into independently executable batches.
/// </summary>
/// <remarks>
/// Each slice keeps the indexes the planner assigned across the whole request, so an index still identifies
/// one check no matter which slice raised it. That matters because slices co-batched into one command share a
/// single provider exception: zero-basing each slice would make two different checks both report index 0. The
/// compiler accepts a slice whose indexes run contiguously from any starting value, and the failure mapper is
/// always given the request's full planned list.
/// </remarks>
internal static class CustomViewAuthorizationCheckSplitter
{
    /// <summary>
    /// Partitions <paramref name="checks"/> by whether the CMS configured them at or before
    /// <paramref name="configuredIndex"/>.
    /// </summary>
    /// <remarks>
    /// Custom views and <c>NamespaceBased</c> are AND filters that execute in CMS-configured order, and the
    /// first failure is the one reported. A namespace check therefore has to run between the custom views
    /// configured before it and those configured after, which is only possible if each side is its own batch.
    /// Ties go to the custom view: the namespace planner stamps every namespace check with the earliest index
    /// at which <c>NamespaceBased</c> appears, so an equal index means the same configured position, and
    /// ordering within it is arbitrary.
    /// </remarks>
    public static (
        IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> Before,
        IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> After
    ) PartitionByConfiguredIndex(
        IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> checks,
        int configuredIndex
    ) => PartitionByConfiguredIndex(checks, static check => check, configuredIndex);

    /// <summary>
    /// Partitions items that carry a check — a check paired with an extracted proposed value, for instance —
    /// by the same rule, so the tie rule has one definition.
    /// </summary>
    public static (IReadOnlyList<T> Before, IReadOnlyList<T> After) PartitionByConfiguredIndex<T>(
        IReadOnlyList<T> items,
        Func<T, SingleRecordCustomViewAuthorizationCheckSpec> checkSelector,
        int configuredIndex
    )
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(checkSelector);

        List<T> before = [];
        List<T> after = [];

        foreach (var item in items)
        {
            if (checkSelector(item).ConfiguredStrategy.RawConfiguredIndex <= configuredIndex)
            {
                before.Add(item);
            }
            else
            {
                after.Add(item);
            }
        }

        return (before, after);
    }
}
