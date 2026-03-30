// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

internal static class RelationalWriteSupport
{
    private const string DescriptorWriteStoryRef = "E07-S06 (06-descriptor-writes.md)";

    public static ResourceWritePlan GetWritePlanOrThrow(MappingSet mappingSet, QualifiedResourceName resource)
    {
        ArgumentNullException.ThrowIfNull(mappingSet);

        if (mappingSet.WritePlansByResource.TryGetValue(resource, out var writePlan))
        {
            return writePlan;
        }

        var concreteResourceModel = mappingSet.Model.ConcreteResourcesInNameOrder.SingleOrDefault(model =>
            model.ResourceKey.Resource == resource
        );

        if (concreteResourceModel is null)
        {
            throw new KeyNotFoundException(
                $"Mapping set '{FormatMappingSetKey(mappingSet.Key)}' does not contain resource '{FormatResource(resource)}' in ConcreteResourcesInNameOrder."
            );
        }

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

    public static short GetResourceKeyIdOrThrow(MappingSet mappingSet, QualifiedResourceName resource)
    {
        ArgumentNullException.ThrowIfNull(mappingSet);

        if (mappingSet.ResourceKeyIdByResource.TryGetValue(resource, out var resourceKeyId))
        {
            return resourceKeyId;
        }

        throw new KeyNotFoundException(
            $"Mapping set '{FormatMappingSetKey(mappingSet.Key)}' does not contain a resource key id for resource '{FormatResource(resource)}'. "
                + "This indicates an internal compilation/selection bug."
        );
    }

    public static QualifiedResourceName ToQualifiedResourceName(BaseResourceInfo resourceInfo) =>
        new(resourceInfo.ProjectName.Value, resourceInfo.ResourceName.Value);

    public static string FormatResource(QualifiedResourceName resource) =>
        $"{resource.ProjectName}.{resource.ResourceName}";

    public static string BuildWriteExecutionNotImplementedMessage(
        RelationalWriteOperationKind operationKind,
        QualifiedResourceName resource
    )
    {
        var operationLabel = operationKind switch
        {
            RelationalWriteOperationKind.Post => "POST",
            RelationalWriteOperationKind.Put => "PUT",
            _ => throw new ArgumentOutOfRangeException(nameof(operationKind), operationKind, null),
        };

        return $"Relational {operationLabel} terminal write stage is not implemented for resource '{FormatResource(resource)}'. "
            + "Write-plan selection, target-context resolution, reference resolution, and flattening succeeded, but relational command execution is still pending.";
    }

    public static string FormatMappingSetKey(MappingSetKey key) =>
        $"{key.EffectiveSchemaHash}/{key.Dialect}/{key.RelationalMappingVersion}";
}
