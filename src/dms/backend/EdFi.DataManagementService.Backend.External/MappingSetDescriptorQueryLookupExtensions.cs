// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Lookup helpers for descriptor-endpoint query capability metadata with actionable failure reasons when support was intentionally omitted.
/// </summary>
public static class MappingSetDescriptorQueryLookupExtensions
{
    /// <summary>
    /// Gets compiled descriptor-endpoint query capability metadata for <paramref name="resource" /> or throws a deterministic actionable exception.
    /// </summary>
    public static DescriptorQueryCapability GetDescriptorQueryCapabilityOrThrow(
        this MappingSet mappingSet,
        QualifiedResourceName resource
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);

        if (mappingSet.DescriptorQueryCapabilitiesByResource.TryGetValue(resource, out var queryCapability))
        {
            if (queryCapability.Support is DescriptorQuerySupport.Supported)
            {
                return queryCapability;
            }

            var omission = ((DescriptorQuerySupport.Omitted)queryCapability.Support).Omission;

            throw new NotSupportedException(
                $"Descriptor query capability for resource '{FormatResource(resource)}' was intentionally omitted: "
                    + omission.Reason
            );
        }

        var concreteResourceModel = mappingSet.GetConcreteResourceModelOrThrow(resource);

        if (concreteResourceModel.StorageKind is ResourceStorageKind.SharedDescriptorTable)
        {
            throw new MissingDescriptorQueryCapabilityLookupGuardRailException(
                $"Descriptor query capability lookup failed for resource '{FormatResource(resource)}' in mapping set "
                    + $"'{FormatMappingSetKey(mappingSet.Key)}': resource storage kind "
                    + $"'{ResourceStorageKind.SharedDescriptorTable}' should always have compiled descriptor query capability metadata, "
                    + "including intentional omission state when applicable, but no entry was found. This indicates an internal "
                    + "compilation/selection bug."
            );
        }

        throw new InvalidOperationException(
            $"Descriptor query capability lookup failed for resource '{FormatResource(resource)}' in mapping set "
                + $"'{FormatMappingSetKey(mappingSet.Key)}': storage kind '{concreteResourceModel.StorageKind}' "
                + "does not use the descriptor endpoint query path."
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
