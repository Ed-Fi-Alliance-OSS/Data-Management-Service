// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// One emitted run of proposed custom view-based authorization: the checks in this run paired with the basis
/// values read from the finalized root row, plus the request's full planned list for failure mapping.
/// </summary>
/// <param name="PlannedChecks">
/// Every custom-view check planned for the request, across both value sources, indexed as the planner assigned
/// them. A <c>cv1</c> payload carries only an index, so it is always resolved against this list and never
/// against the run — one request can emit several runs, and their indexes must not collide.
/// </param>
/// <param name="Values">The checks this run emits, in emission order, each with its bound basis value.</param>
internal sealed record ProposedCustomViewAuthorizationRuntimeCheck(
    IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> PlannedChecks,
    IReadOnlyList<ProposedCustomViewRuntimeValue> Values
)
{
    public IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> Checks =>
        [.. Values.Select(static value => value.Check)];
}

/// <param name="Command">The compiled statement.</param>
/// <param name="ResultSetCount">One result set per emitted check.</param>
internal sealed record ProposedCustomViewAuthorizationStatement(
    RelationalCommand Command,
    int ResultSetCount
);

/// <summary>
/// Builds and reads the proposed custom view-based authorization statement. Unlike the stored variant, each
/// check binds a basis value taken from the finalized merged root row rather than the target's
/// <c>DocumentId</c>, so nothing here depends on a captured target.
/// </summary>
internal static class ProposedCustomViewAuthorizationCommand
{
    /// <summary>
    /// Builds the run's statement, or <see langword="null"/> when it emits none. A run holds no self-basis
    /// check, so an empty result means the run itself was empty.
    /// </summary>
    public static ProposedCustomViewAuthorizationStatement? Build(
        MappingSet mappingSet,
        ProposedCustomViewAuthorizationRuntimeCheck runtimeCheck
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(runtimeCheck);

        var sqlPlan = new SingleRecordCustomViewAuthorizationSqlCompiler(mappingSet.Key.Dialect).Compile(
            new SingleRecordCustomViewAuthorizationSqlSpec(
                runtimeCheck.Checks,
                CustomViewAuthorizationSqlSpecDefaults.DocumentIdParameterName
            )
        );

        if (sqlPlan.EmittedCheckIndexesInOrder.Count == 0)
        {
            return null;
        }

        return new ProposedCustomViewAuthorizationStatement(
            new RelationalCommand(sqlPlan.AuthorizationSql, BuildParameters(sqlPlan, runtimeCheck)),
            sqlPlan.EmittedCheckIndexesInOrder.Count
        );
    }

    /// <summary>
    /// Binds one parameter per proposed basis value. A proposed-only run binds no stored <c>DocumentId</c>, so
    /// any parameter the compiler did not tag as a proposed value has no value to take and is a planning
    /// defect rather than something to guess at.
    /// </summary>
    private static IReadOnlyList<RelationalParameter> BuildParameters(
        SingleRecordCustomViewAuthorizationSqlPlan sqlPlan,
        ProposedCustomViewAuthorizationRuntimeCheck runtimeCheck
    )
    {
        var valuesByCheckIndex = runtimeCheck.Values.ToDictionary(
            static value => value.Check.Index,
            static value => value.BasisValue
        );
        var checkIndexByParameterName = sqlPlan.ProposedValueParametersInOrder.ToDictionary(
            static parameter => parameter.ParameterName,
            static parameter => parameter.CheckIndex,
            StringComparer.Ordinal
        );

        List<RelationalParameter> parameters = new(sqlPlan.ParametersInOrder.Count);

        foreach (var parameter in sqlPlan.ParametersInOrder)
        {
            if (parameter.Binding.Kind is not QuerySqlParameterBindingKind.Scalar)
            {
                throw new InvalidOperationException(
                    $"Proposed custom view authorization parameter '{parameter.ParameterName}' must bind as a scalar."
                );
            }

            if (!checkIndexByParameterName.TryGetValue(parameter.ParameterName, out var checkIndex))
            {
                throw new InvalidOperationException(
                    $"Proposed custom view authorization parameter '{parameter.ParameterName}' is not a proposed basis value; a proposed-value run binds no other parameter."
                );
            }

            if (!valuesByCheckIndex.TryGetValue(checkIndex, out var basisValue))
            {
                throw new InvalidOperationException(
                    $"Proposed custom view authorization has no extracted basis value for check '{checkIndex}'."
                );
            }

            parameters.Add(new RelationalParameter($"@{parameter.ParameterName}", basisValue));
        }

        return parameters;
    }
}
