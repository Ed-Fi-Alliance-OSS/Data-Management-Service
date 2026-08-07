// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Plans;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Splits a planned custom-view check list into independently executable batches, reindexing each so it can be
/// compiled and mapped on its own.
/// </summary>
/// <remarks>
/// Reindexing is mandatory, not cosmetic. The SQL compiler requires each check's index to equal its emission
/// position, and the failure mapper resolves a <c>cv1</c> payload positionally against the list it was given.
/// A batch therefore has to be handed the same contiguous list that produced it; passing a sliced list with
/// its original indexes would either fail compilation or resolve a denial to the wrong check.
/// </remarks>
internal static class CustomViewAuthorizationCheckSplitter
{
    /// <summary>
    /// Reindexes <paramref name="checks"/> to <c>0..n-1</c> in their current order.
    /// </summary>
    public static IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> Reindex(
        IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> checks
    )
    {
        ArgumentNullException.ThrowIfNull(checks);

        return [.. checks.Select(static (check, position) => check with { Index = position })];
    }

    /// <summary>
    /// Partitions <paramref name="checks"/} by whether the CMS configured them before
    /// <paramref name="configuredIndex"/>, reindexing both sides.
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
    )
    {
        ArgumentNullException.ThrowIfNull(checks);

        List<SingleRecordCustomViewAuthorizationCheckSpec> before = [];
        List<SingleRecordCustomViewAuthorizationCheckSpec> after = [];

        foreach (var check in checks)
        {
            if (check.ConfiguredStrategy.RawConfiguredIndex <= configuredIndex)
            {
                before.Add(check);
            }
            else
            {
                after.Add(check);
            }
        }

        return (Reindex(before), Reindex(after));
    }
}
