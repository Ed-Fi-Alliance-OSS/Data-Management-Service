// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Lookup helpers for resource write plans with actionable failure reasons when a plan was intentionally omitted.
/// </summary>
public static class MappingSetWriteLookupExtensions
{
    private const string DescriptorWriteStoryRef = "E07-S06 (06-descriptor-writes.md)";
    private static readonly ConditionalWeakTable<
        MappingSet,
        IReadOnlyDictionary<QualifiedResourceName, ConcreteResourceModel>
    > ConcreteResourceModelsByResource = new();

    /// <summary>
    /// Returns <c>true</c> when <paramref name="resource" /> is stored in the shared
    /// <c>dms.Descriptor</c> table, providing the <paramref name="descriptorResourceModel" />
    /// with its <see cref="DescriptorMetadata" />.
    /// </summary>
    public static bool TryGetDescriptorResourceModel(
        this MappingSet mappingSet,
        QualifiedResourceName resource,
        [NotNullWhen(true)] out ConcreteResourceModel? descriptorResourceModel
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);

        if (
            TryGetConcreteResourceModel(mappingSet, resource, out var model)
            && model.StorageKind == ResourceStorageKind.SharedDescriptorTable
        )
        {
            descriptorResourceModel = model;
            return true;
        }

        descriptorResourceModel = null;
        return false;
    }

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
            throw new MissingWritePlanLookupGuardRailException(
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
    /// Attempts to resolve the concrete resource model from the mapping set's canonical resource list.
    /// </summary>
    private static bool TryGetConcreteResourceModel(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        [NotNullWhen(true)] out ConcreteResourceModel? model
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

        return concreteResourcesByResource.TryGetValue(resource, out model);
    }

    /// <summary>
    /// Resolves the concrete resource model from the mapping set's canonical resource list or throws a deterministic error.
    /// </summary>
    public static ConcreteResourceModel GetConcreteResourceModelOrThrow(
        this MappingSet mappingSet,
        QualifiedResourceName resource
    )
    {
        if (TryGetConcreteResourceModel(mappingSet, resource, out var model))
        {
            return model;
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
