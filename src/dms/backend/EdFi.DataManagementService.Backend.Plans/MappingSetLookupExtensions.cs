// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Lookup helpers for resource read/write plans with actionable failure reasons when a plan was intentionally omitted.
/// </summary>
public static class MappingSetLookupExtensions
{
    private const string WriteCollectionsAndKeyUnificationStoryRef =
        "E15-S04 (04-write-plan-compiler-collections-and-extensions.md)";
    private const string ReadHydrationStoryRef = "E15-S05 (05-read-plan-compiler-hydration.md)";
    private const string DescriptorWriteStoryRef = "E07-S06 (06-descriptor-writes.md)";
    private const string DescriptorReadStoryRef = "E08-S05 (05-descriptor-endpoints.md)";

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
        var resourceModel = concreteResourceModel.RelationalModel;

        if (concreteResourceModel.StorageKind == ResourceStorageKind.SharedDescriptorTable)
        {
            throw new NotSupportedException(
                $"Write plan for resource '{FormatResource(resource)}' was intentionally omitted: "
                    + $"storage kind '{ResourceStorageKind.SharedDescriptorTable}' does not use thin-slice relational-table write plans. "
                    + $"Next story: {DescriptorWriteStoryRef}."
            );
        }

        var supportResult = ThinSliceWritePlanSupportEvaluator.Evaluate(resourceModel);

        switch (supportResult.UnsupportedReason)
        {
            case ThinSliceWritePlanUnsupportedReason.None:
                break;
            case ThinSliceWritePlanUnsupportedReason.NonRootOnly:
                throw new NotSupportedException(
                    $"Write plan for resource '{FormatResource(resource)}' was intentionally omitted: "
                        + "thin-slice write compilation supports only root-only resources "
                        + $"(TablesInDependencyOrder.Count == 1, actual {supportResult.TableCount}). "
                        + $"Next story: {WriteCollectionsAndKeyUnificationStoryRef}."
                );
            case ThinSliceWritePlanUnsupportedReason.RootHasKeyUnificationClasses:
                throw new NotSupportedException(
                    $"Write plan for resource '{FormatResource(resource)}' was intentionally omitted: "
                        + $"root table '{resourceModel.Root.Table}' has {supportResult.RootKeyUnificationClassCount} key-unification class(es). "
                        + $"Next story: {WriteCollectionsAndKeyUnificationStoryRef}."
                );
            case ThinSliceWritePlanUnsupportedReason.RootHasStoredNonKeyColumnsWithoutSourceJsonPath:
                throw new NotSupportedException(
                    $"Write plan for resource '{FormatResource(resource)}' was intentionally omitted: "
                        + $"root table '{resourceModel.Root.Table}' has {supportResult.RootStoredNonKeyColumnsWithoutSourceJsonPathCount} stored non-key column(s) without SourceJsonPath "
                        + "(precomputed/key-unification candidates). "
                        + $"Next story: {WriteCollectionsAndKeyUnificationStoryRef}."
                );
            case ThinSliceWritePlanUnsupportedReason.NonRelationalStorage:
                throw new NotSupportedException(
                    $"Write plan for resource '{FormatResource(resource)}' was intentionally omitted: "
                        + $"storage kind '{supportResult.StorageKind}' is out of thin-slice write runtime compilation scope. "
                        + $"Next story: {WriteCollectionsAndKeyUnificationStoryRef}."
                );
            default:
                throw new InvalidOperationException(
                    $"Write plan lookup failed for resource '{FormatResource(resource)}': "
                        + $"unsupported reason '{supportResult.UnsupportedReason}' is not recognized."
                );
        }

        throw new InvalidOperationException(
            $"Write plan lookup failed for resource '{FormatResource(resource)}': "
                + "resource exists and appears thin-slice compatible, but no compiled write plan is present."
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
            return readPlan;
        }

        var concreteResourceModel = GetConcreteResourceModelOrThrow(mappingSet, resource);

        if (concreteResourceModel.StorageKind == ResourceStorageKind.SharedDescriptorTable)
        {
            throw new NotSupportedException(
                $"Read plan for resource '{FormatResource(resource)}' was intentionally omitted: "
                    + $"storage kind '{ResourceStorageKind.SharedDescriptorTable}' does not use thin-slice relational-table hydration plans. "
                    + $"Next story: {DescriptorReadStoryRef}."
            );
        }

        var resourceModel = concreteResourceModel.RelationalModel;

        if (resourceModel.TablesInDependencyOrder.Count != 1)
        {
            throw new NotSupportedException(
                $"Read plan for resource '{FormatResource(resource)}' was intentionally omitted: "
                    + "thin-slice read compilation supports only root-only resources "
                    + $"(TablesInDependencyOrder.Count == 1, actual {resourceModel.TablesInDependencyOrder.Count}). "
                    + $"Next story: {ReadHydrationStoryRef}."
            );
        }

        throw new InvalidOperationException(
            $"Read plan lookup failed for resource '{FormatResource(resource)}': "
                + "resource exists and appears thin-slice compatible, but no compiled read plan is present."
        );
    }

    private static ConcreteResourceModel GetConcreteResourceModelOrThrow(
        MappingSet mappingSet,
        QualifiedResourceName resource
    )
    {
        foreach (var concreteResourceModel in mappingSet.Model.ConcreteResourcesInNameOrder)
        {
            if (concreteResourceModel.RelationalModel.Resource.Equals(resource))
            {
                return concreteResourceModel;
            }
        }

        throw new KeyNotFoundException(
            $"Mapping set '{FormatMappingSetKey(mappingSet.Key)}' does not contain resource '{FormatResource(resource)}' in ConcreteResourcesInNameOrder."
        );
    }

    private static string FormatResource(QualifiedResourceName resource)
    {
        return $"{resource.ProjectName}.{resource.ResourceName}";
    }

    private static string FormatMappingSetKey(MappingSetKey key)
    {
        return $"{key.EffectiveSchemaHash}/{key.Dialect}/{key.RelationalMappingVersion}";
    }
}
