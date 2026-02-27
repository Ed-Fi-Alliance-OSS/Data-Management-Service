// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;

namespace EdFi.DataManagementService.Backend.External.Plans;

/// <summary>
/// Helpers for materializing plan-contract collections into immutable arrays.
/// </summary>
/// <remarks>
/// Plan contracts are executor-facing, "plain data" shapes that must preserve authoritative ordering for deterministic
/// bindings and AOT payload reconstruction.
/// </remarks>
internal static class PlanContractCollectionCloner
{
    /// <summary>
    /// Materializes an <see cref="ImmutableArray{T}"/> from the supplied enumerable.
    /// </summary>
    /// <typeparam name="T">Element type.</typeparam>
    /// <param name="values">The values to materialize.</param>
    /// <param name="parameterName">Parameter name used for argument validation.</param>
    /// <returns>An immutable array containing the values in enumeration order.</returns>
    public static ImmutableArray<T> ToImmutableArray<T>(IEnumerable<T> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);

        return [.. values];
    }
}
