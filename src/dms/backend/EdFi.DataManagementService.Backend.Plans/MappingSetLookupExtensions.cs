// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Lookup helpers for resource read plans with actionable failure reasons when a plan was intentionally omitted.
/// </summary>
public static class MappingSetLookupExtensions
{
    private static readonly ConditionalWeakTable<
        MappingSet,
        ConcurrentDictionary<QualifiedResourceName, ResourceReadPlan>
    > ValidatedReadPlansByResource = new();

    /// <summary>
    /// Gets the compiled read plan for <paramref name="resource" /> or throws a deterministic actionable exception.
    /// </summary>
    public static ResourceReadPlan GetReadPlanOrThrow(
        this MappingSet mappingSet,
        QualifiedResourceName resource
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);

        if (mappingSet.ReadPlansByResource.TryGetValue(resource, out var readPlan))
        {
            return GetValidatedReadPlanOrThrow(mappingSet, resource, readPlan);
        }

        var concreteResourceModel = mappingSet.GetConcreteResourceModelOrThrow(resource);

        if (concreteResourceModel.StorageKind == ResourceStorageKind.SharedDescriptorTable)
        {
            throw new NotSupportedException(
                $"Read plan for resource '{FormatResource(resource)}' was intentionally omitted: "
                    + $"storage kind '{ResourceStorageKind.SharedDescriptorTable}' uses the descriptor read path instead of compiled relational-table hydration plans."
            );
        }

        if (concreteResourceModel.StorageKind == ResourceStorageKind.RelationalTables)
        {
            throw new InvalidOperationException(
                $"Read plan lookup failed for resource '{FormatResource(resource)}' in mapping set "
                    + $"'{FormatMappingSetKey(mappingSet.Key)}': resource storage kind "
                    + $"'{ResourceStorageKind.RelationalTables}' should always have a compiled relational-table read plan, but no entry "
                    + "was found. This indicates an internal compilation/selection bug."
            );
        }

        throw new InvalidOperationException(
            $"Read plan lookup failed for resource '{FormatResource(resource)}' in mapping set "
                + $"'{FormatMappingSetKey(mappingSet.Key)}': storage kind '{concreteResourceModel.StorageKind}' "
                + "is not recognized."
        );
    }

    private static ResourceReadPlan GetValidatedReadPlanOrThrow(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        ResourceReadPlan readPlan
    )
    {
        var validatedReadPlansByResource = ValidatedReadPlansByResource.GetValue(
            mappingSet,
            static _ => new ConcurrentDictionary<QualifiedResourceName, ResourceReadPlan>()
        );

        return validatedReadPlansByResource.GetOrAdd(
            resource,
            static (validatedResource, state) =>
            {
                var (validatedMappingSet, cachedReadPlan) = state;

                ReadPlanProjectionContractValidator.ValidateOrThrow(
                    cachedReadPlan,
                    reason => new InvalidOperationException(
                        $"Read plan lookup failed for resource '{FormatResource(validatedResource)}' in mapping set "
                            + $"'{FormatMappingSetKey(validatedMappingSet.Key)}': compiled relational-table read plan has invalid projection metadata. "
                            + $"{reason}. This indicates an internal compilation/selection bug."
                    )
                );

                return cachedReadPlan;
            },
            (mappingSet, readPlan)
        );
    }

    /// <summary>
    /// Formats a qualified resource name as <c>{ProjectName}.{ResourceName}</c> for diagnostics.
    /// </summary>
    private static string FormatResource(QualifiedResourceName resource)
    {
        return $"{resource.ProjectName}.{resource.ResourceName}";
    }

    /// <summary>
    /// Formats a mapping set key as <c>{EffectiveSchemaHash}/{Dialect}/{RelationalMappingVersion}</c> for diagnostics.
    /// </summary>
    private static string FormatMappingSetKey(MappingSetKey key)
    {
        return $"{key.EffectiveSchemaHash}/{key.Dialect}/{key.RelationalMappingVersion}";
    }
}
