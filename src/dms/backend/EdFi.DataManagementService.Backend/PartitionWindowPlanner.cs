// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// A compiled partition-boundary statement and the values for every parameter it binds.
/// </summary>
/// <param name="Plan">
/// The compiled boundary statement. It carries the candidate relation's filter parameters plus the
/// requested count and minimum size, so it binds through the same parameter binder a page does.
/// </param>
/// <param name="ParameterValues">Values for every parameter the plan binds.</param>
internal sealed record PartitionWindowPlan(
    PageDocumentIdSqlPlan Plan,
    IReadOnlyDictionary<string, object?> ParameterValues
);

/// <summary>
/// Pairs an already-compiled unpaged candidate relation with the two values the partition-boundary
/// statement binds on top of it.
/// </summary>
/// <remarks>
/// Deliberately narrow: it neither plans predicates nor resolves authorization. The candidate relation
/// arrives compiled from the same page keyset planner a GET-many uses, which is what makes a partition
/// calculated over exactly the rows a page of the same request would be selected from.
/// </remarks>
internal sealed class PartitionWindowPlanner(SqlDialect dialect)
{
    private readonly PartitionWindowSqlCompiler _sqlCompiler = new(dialect);

    /// <summary>
    /// Compiles the boundary statement over <paramref name="candidatePlan" /> and binds the requested
    /// count and minimum size to the names the candidate relation reserved for them.
    /// </summary>
    /// <param name="candidatePlan">The compiled unpaged candidate relation and its bound filter values.</param>
    /// <param name="requestedPartitionCount">
    /// The desired partition count. Core validates and defaults it, so a value below one means the
    /// request reached the backend without that validation rather than that a client sent one.
    /// </param>
    /// <param name="minimumPartitionSize">The smallest partition, in candidate rows.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when either value is below one. Both divide or clamp inside the statement, so binding a
    /// zero would divide by zero at the provider and a negative would make every row a boundary.
    /// </exception>
    public PartitionWindowPlan Plan(
        CandidateQueryPlan candidatePlan,
        int requestedPartitionCount,
        long minimumPartitionSize
    )
    {
        ArgumentNullException.ThrowIfNull(candidatePlan);
        ArgumentOutOfRangeException.ThrowIfLessThan(requestedPartitionCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumPartitionSize, 1);

        var mode = PageCandidateModePlanning.UnpagedCandidatesMode;
        var plan = _sqlCompiler.Compile(candidatePlan.Plan, mode);

        // Both are bound as Int64 so the statement's division and modulo stay in the width the row
        // numbering uses. A narrower count would leave the division operand width provider-inferred.
        Dictionary<string, object?> parameterValues = new(
            candidatePlan.ParameterValues,
            StringComparer.Ordinal
        )
        {
            [mode.PartitionCountParameterName] = (long)requestedPartitionCount,
            [mode.MinimumPartitionSizeParameterName] = minimumPartitionSize,
        };

        return new PartitionWindowPlan(plan, parameterValues);
    }
}
