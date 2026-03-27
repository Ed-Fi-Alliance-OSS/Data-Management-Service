// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// Identifies a compiled mapping set selection key.
/// </summary>
/// <param name="EffectiveSchemaHash">The effective schema hash (lowercase hex).</param>
/// <param name="Dialect">The SQL dialect.</param>
/// <param name="RelationalMappingVersion">The relational mapping version label.</param>
public readonly record struct MappingSetKey(
    string EffectiveSchemaHash,
    SqlDialect Dialect,
    string RelationalMappingVersion
);

/// <summary>
/// Compiled mapping set used by runtime plan execution.
/// </summary>
/// <param name="Key">The mapping set selection key.</param>
/// <param name="Model">The derived relational model set used to compile plans.</param>
/// <param name="WritePlansByResource">Write plans keyed by qualified resource name.</param>
/// <param name="ReadPlansByResource">Read plans keyed by qualified resource name.</param>
/// <param name="ResourceKeyIdByResource">Resource key identifiers keyed by qualified resource name.</param>
/// <param name="ResourceKeyById">Resource key entries keyed by resource key id.</param>
/// <param name="SecurableElementColumnPathsByResource">
/// Precomputed securable element authorization column paths keyed by qualified resource name.
/// Each value is a list of <see cref="ColumnPathStep"/> chains representing the join path
/// to reach the authorization column.
/// </param>
public sealed record MappingSet(
    MappingSetKey Key,
    DerivedRelationalModelSet Model,
    IReadOnlyDictionary<QualifiedResourceName, ResourceWritePlan> WritePlansByResource,
    IReadOnlyDictionary<QualifiedResourceName, ResourceReadPlan> ReadPlansByResource,
    IReadOnlyDictionary<QualifiedResourceName, short> ResourceKeyIdByResource,
    IReadOnlyDictionary<short, ResourceKeyEntry> ResourceKeyById,
    IReadOnlyDictionary<
        QualifiedResourceName,
        IReadOnlyList<IReadOnlyList<ColumnPathStep>>
    > SecurableElementColumnPathsByResource
)
{
    private const string AotMappingPackDecodeStoryRef =
        "reference/design/backend-redesign/epics/05-mpack-generation/05-pack-loader-validation.md";

    /// <summary>
    /// Creates a mapping set from an AOT mapping-pack payload.
    /// </summary>
    /// <param name="payload">The decoded mapping-pack payload.</param>
    /// <returns>The materialized mapping set.</returns>
    /// <exception cref="NotSupportedException">
    /// Thrown until AOT mapping-pack decode support is implemented.
    /// </exception>
    public static MappingSet FromPayload(MappingPackPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        throw new NotSupportedException(
            "AOT mapping-pack decode is not implemented yet for MappingSet.FromPayload(MappingPackPayload). "
                + $"See E05-S05: {AotMappingPackDecodeStoryRef}."
        );
    }
}

/// <summary>
/// Placeholder mapping-pack payload contract.
/// </summary>
public sealed record MappingPackPayload;
