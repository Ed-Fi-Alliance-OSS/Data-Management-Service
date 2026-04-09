// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

internal static class RelationalWriteSupport
{
    public static TriggerKindParameters.ReferentialIdentityMaintenance GetReferentialIdentityParametersOrThrow(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        DbTableName rootTable
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);

        var referentialIdentityTrigger = mappingSet.Model.TriggersInCreateOrder.SingleOrDefault(trigger =>
            trigger.Table.Equals(rootTable)
            && trigger.Parameters is TriggerKindParameters.ReferentialIdentityMaintenance parameters
            && string.Equals(parameters.ProjectName, resource.ProjectName, StringComparison.Ordinal)
            && string.Equals(parameters.ResourceName, resource.ResourceName, StringComparison.Ordinal)
        );

        if (
            referentialIdentityTrigger?.Parameters
            is TriggerKindParameters.ReferentialIdentityMaintenance referentialIdentityParameters
        )
        {
            return referentialIdentityParameters;
        }

        throw new InvalidOperationException(
            BuildMissingReferentialIdentityTriggerMetadataMessage(mappingSet.Key, resource)
        );
    }

    public static RelationalWriteTargetContext? TryTranslateTargetContext(
        RelationalWriteTargetLookupResult targetLookupResult
    )
    {
        ArgumentNullException.ThrowIfNull(targetLookupResult);

        return targetLookupResult switch
        {
            RelationalWriteTargetLookupResult.CreateNew(var documentUuid) =>
                new RelationalWriteTargetContext.CreateNew(documentUuid),
            RelationalWriteTargetLookupResult.ExistingDocument(
                var documentId,
                var documentUuid,
                var observedContentVersion
            ) => new RelationalWriteTargetContext.ExistingDocument(
                documentId,
                documentUuid,
                observedContentVersion
            ),
            RelationalWriteTargetLookupResult.NotFound => null,
            _ => throw new InvalidOperationException(
                $"Relational target lookup translation does not support result type '{targetLookupResult.GetType().Name}'."
            ),
        };
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

    public static string BuildMissingExistingDocumentReadPlanMessage(QualifiedResourceName resource) =>
        $"Relational write executor requires a compiled relational-table read plan for existing-document writes on resource '{FormatResource(resource)}'. "
        + "This indicates an internal request-shaping or guard-rail bug.";

    public static string BuildImmutableIdentityFailureMessage(QualifiedResourceName resource) =>
        $"Identifying values for the {resource.ResourceName} resource cannot be changed. Delete and recreate the resource item instead.";

    public static string BuildIdentityUpdatesNotYetSupportedMessage(QualifiedResourceName resource) =>
        $"Relational existing-document writes do not yet support identity-changing operations for resource '{FormatResource(resource)}' when allowIdentityUpdates=true. "
        + "Keep the identity projection stable until the strict identity-maintenance work lands.";

    public static string BuildMissingReferentialIdentityTriggerMetadataMessage(
        MappingSetKey mappingSetKey,
        QualifiedResourceName resource
    ) =>
        $"Mapping set '{FormatMappingSetKey(mappingSetKey)}' "
        + $"is missing referential-identity trigger metadata for resource '{FormatResource(resource)}'.";

    public static string FormatMappingSetKey(MappingSetKey key) =>
        $"{key.EffectiveSchemaHash}/{key.Dialect}/{key.RelationalMappingVersion}";
}
