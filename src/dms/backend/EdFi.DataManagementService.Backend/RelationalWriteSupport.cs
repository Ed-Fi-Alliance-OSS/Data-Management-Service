// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

internal static class RelationalWriteSupport
{
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
