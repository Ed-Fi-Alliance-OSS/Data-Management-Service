// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Plans;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Binds a compiled partition-boundary statement's parameters.
/// </summary>
/// <remarks>
/// Shared by the regular-resource and descriptor entry points so both bind the plan's own inventory
/// through the same binder a page uses. The two entry points differ in how the candidate relation is
/// planned, never in how the compiled statement is bound.
/// </remarks>
internal static class PartitionBoundaryParameterBinding
{
    public static IReadOnlyList<RelationalParameter> Bind(
        PartitionWindowPlan partitionPlan,
        string commandDescription
    )
    {
        ArgumentNullException.ThrowIfNull(partitionPlan);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandDescription);

        return
        [
            .. PlannedQueryParameterBinder
                .BindParameters(
                    partitionPlan.Plan,
                    partitionPlan.ParameterValues,
                    commandDescription,
                    $"{commandDescription} parameter",
                    $"Unsupported parameter binding kind for {commandDescription}."
                )
                .Select(static binding => new RelationalParameter(
                    binding.Name,
                    binding.Value,
                    binding.ConfigureParameter
                )),
        ];
    }
}

/// <summary>
/// Reads the starting identifiers a partition-boundary statement returns.
/// </summary>
/// <remarks>
/// The statement selects one identifiers-only column ordered ascending, so this reads exactly that and
/// asserts nothing about the values. <see cref="PartitionRangeAssembler" /> owns the ascent invariant,
/// which keeps the reader from silently accepting an ordering the statement stopped producing.
/// </remarks>
internal static class PartitionBoundaryReader
{
    public static async Task<IReadOnlyList<long>> ReadAscendingStartsAsync(
        IRelationalCommandReader reader,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(reader);

        List<long> ascendingStarts = [];

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ascendingStarts.Add(reader.GetFieldValue<long>(0));
        }

        return ascendingStarts;
    }
}
