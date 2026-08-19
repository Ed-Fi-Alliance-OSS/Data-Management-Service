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
/// Executes a compiled partition-boundary statement and reads the ordered starting identifiers.
/// </summary>
/// <remarks>
/// The regular-resource and descriptor entry points differ in how the candidate relation is planned and
/// in how a provider fault is classified, never in how the compiled statement is executed. Keeping the
/// single command here means the binder, the reader, and the one command that uses them stay in one
/// file, which is the property the binder was extracted for.
///
/// The command description is the caller's, because it names the operation in the diagnostics the
/// binder and the executor emit: a boundary statement from the descriptor path has to stay
/// distinguishable from a regular-resource one in a log.
/// </remarks>
internal static class PartitionBoundaryCommand
{
    public static Task<IReadOnlyList<long>> ExecuteAsync(
        IRelationalCommandExecutor commandExecutor,
        PartitionWindowPlan partitionPlan,
        string commandDescription,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(commandExecutor);
        ArgumentNullException.ThrowIfNull(partitionPlan);

        var command = new RelationalCommand(
            partitionPlan.Plan.PageDocumentIdSql,
            PartitionBoundaryParameterBinding.Bind(partitionPlan, commandDescription)
        );

        return commandExecutor.ExecuteReaderAsync(
            command,
            PartitionBoundaryReader.ReadAscendingStartsAsync,
            cancellationToken
        );
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
