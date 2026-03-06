// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Lookup helpers for resource read/write plans with actionable failure reasons when a plan was intentionally omitted.
/// </summary>
public static class MappingSetLookupExtensions
{
    private const string DescriptorWriteStoryRef = "E07-S06 (06-descriptor-writes.md)";
    private const string DescriptorReadStoryRef = "E08-S05 (05-descriptor-endpoints.md)";
    private const string ProjectionReadStoryRef = "E15-S06 (06-projection-plan-compilers.md)";
    private static readonly ConditionalWeakTable<
        MappingSet,
        IReadOnlyDictionary<QualifiedResourceName, ConcreteResourceModel>
    > ConcreteResourceModelsByResource = new();

    /// <summary>
    /// Gets the compiled write plan for <paramref name="resource" /> or throws a deterministic actionable exception.
    /// </summary>
    public static ResourceWritePlan GetWritePlanOrThrow(
        this MappingSet mappingSet,
        QualifiedResourceName resource
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);

        if (mappingSet.WritePlansByResource.TryGetValue(resource, out var writePlan))
        {
            return writePlan;
        }

        var concreteResourceModel = GetConcreteResourceModelOrThrow(mappingSet, resource);

        if (concreteResourceModel.StorageKind == ResourceStorageKind.SharedDescriptorTable)
        {
            throw new NotSupportedException(
                $"Write plan for resource '{FormatResource(resource)}' was intentionally omitted: "
                    + $"storage kind '{ResourceStorageKind.SharedDescriptorTable}' uses the descriptor write path instead of compiled relational-table write plans. "
                    + $"Next story: {DescriptorWriteStoryRef}."
            );
        }

        if (concreteResourceModel.StorageKind == ResourceStorageKind.RelationalTables)
        {
            throw new InvalidOperationException(
                $"Write plan lookup failed for resource '{FormatResource(resource)}' in mapping set "
                    + $"'{FormatMappingSetKey(mappingSet.Key)}': resource storage kind "
                    + $"'{ResourceStorageKind.RelationalTables}' should always have a compiled relational-table write plan, but no entry "
                    + "was found. This indicates an internal compilation/selection bug."
            );
        }

        throw new InvalidOperationException(
            $"Write plan lookup failed for resource '{FormatResource(resource)}' in mapping set "
                + $"'{FormatMappingSetKey(mappingSet.Key)}': storage kind '{concreteResourceModel.StorageKind}' "
                + "is not recognized."
        );
    }

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
            ThrowIfReadPlanRequiresProjectionCompilation(mappingSet, resource, readPlan);
            return readPlan;
        }

        var concreteResourceModel = GetConcreteResourceModelOrThrow(mappingSet, resource);

        if (concreteResourceModel.StorageKind == ResourceStorageKind.SharedDescriptorTable)
        {
            throw new NotSupportedException(
                $"Read plan for resource '{FormatResource(resource)}' was intentionally omitted: "
                    + $"storage kind '{ResourceStorageKind.SharedDescriptorTable}' uses the descriptor read path instead of compiled relational-table hydration plans. "
                    + $"Next story: {DescriptorReadStoryRef}."
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

    private static void ThrowIfReadPlanRequiresProjectionCompilation(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        ResourceReadPlan readPlan
    )
    {
        if (readPlan.Model.StorageKind != ResourceStorageKind.RelationalTables)
        {
            return;
        }

        var requiresReferenceIdentityProjection =
            readPlan.Model.DocumentReferenceBindings.Count > 0
            && readPlan.ReferenceIdentityProjectionPlansInDependencyOrder.IsEmpty;
        var requiresDescriptorProjection =
            readPlan.Model.DescriptorEdgeSources.Count > 0
            && readPlan.DescriptorProjectionPlansInOrder.IsEmpty;

        if (!requiresReferenceIdentityProjection && !requiresDescriptorProjection)
        {
            return;
        }

        var missingProjectionReason = (
            requiresReferenceIdentityProjection,
            requiresDescriptorProjection
        ) switch
        {
            (true, true) =>
                "DocumentReferenceBindings are present while ReferenceIdentityProjectionPlansInDependencyOrder is empty, "
                    + "and DescriptorEdgeSources are present while DescriptorProjectionPlansInOrder is empty",
            (true, false) =>
                "DocumentReferenceBindings are present while ReferenceIdentityProjectionPlansInDependencyOrder is empty",
            (false, true) =>
                "DescriptorEdgeSources are present while DescriptorProjectionPlansInOrder is empty",
            _ => throw new InvalidOperationException(
                $"Read plan projection gating reached an invalid state for resource '{FormatResource(resource)}'."
            ),
        };

        throw new NotSupportedException(
            $"Read plan for resource '{FormatResource(resource)}' in mapping set "
                + $"'{FormatMappingSetKey(mappingSet.Key)}' is not executable yet: hydration SQL was compiled for "
                + $"storage kind '{ResourceStorageKind.RelationalTables}', but {missingProjectionReason}. "
                + $"Story 05 only compiles hydration SQL; story 06 must compile the remaining projection metadata. "
                + $"Next story: {ProjectionReadStoryRef}."
        );
    }

    /// <summary>
    /// Resolves the concrete resource model from the mapping set's canonical resource list or throws a deterministic error.
    /// </summary>
    private static ConcreteResourceModel GetConcreteResourceModelOrThrow(
        MappingSet mappingSet,
        QualifiedResourceName resource
    )
    {
        var concreteResourcesByResource = ConcreteResourceModelsByResource.GetValue(
            mappingSet,
            static staticMappingSet =>
            {
                var resourcesByName = new Dictionary<QualifiedResourceName, ConcreteResourceModel>();

                foreach (var concreteResourceModel in staticMappingSet.Model.ConcreteResourcesInNameOrder)
                {
                    var resource = concreteResourceModel.RelationalModel.Resource;

                    if (!resourcesByName.TryAdd(resource, concreteResourceModel))
                    {
                        throw new InvalidOperationException(
                            $"Mapping set '{FormatMappingSetKey(staticMappingSet.Key)}' contains duplicate resource "
                                + $"'{FormatResource(resource)}' in ConcreteResourcesInNameOrder."
                        );
                    }
                }

                return resourcesByName.ToFrozenDictionary();
            }
        );

        if (concreteResourcesByResource.TryGetValue(resource, out var concreteResourceModel))
        {
            return concreteResourceModel;
        }

        throw new KeyNotFoundException(
            $"Mapping set '{FormatMappingSetKey(mappingSet.Key)}' does not contain resource '{FormatResource(resource)}' in ConcreteResourcesInNameOrder."
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
