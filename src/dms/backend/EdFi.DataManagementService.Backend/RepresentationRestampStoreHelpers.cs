// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.DocumentCache;

namespace EdFi.DataManagementService.Backend;

internal static class RepresentationRestampStoreHelpers
{
    public static RepresentationRestampDocument ToRepresentationDocument(
        MappingSet mappingSet,
        long documentId,
        Guid documentUuid,
        short resourceKeyId
    ) => new(documentId, documentUuid, ResolveRoute(mappingSet, resourceKeyId));

    public static short ResolveResourceKeyId(
        MappingSet mappingSet,
        DocumentCacheRepresentationRestampResourceScope scope
    )
    {
        QualifiedResourceName resource = new(scope.ProjectName, scope.ResourceName);
        if (!mappingSet.ResourceKeyIdByResource.TryGetValue(resource, out short resourceKeyId))
        {
            throw ValidationFailure(
                DocumentCacheAdministrativeCommandClassification.InvalidRepresentationRestampScope,
                DocumentCacheAdministrativeDiagnosticCategory.InvalidRepresentationRestampScope,
                "Representation restamp resource scope did not resolve to a compiled resource key."
            );
        }

        _ = ResolveRoute(mappingSet, resourceKeyId);
        return resourceKeyId;
    }

    public static RepresentationRestampMirrorRoute ResolveRoute(MappingSet mappingSet, short resourceKeyId)
    {
        if (!mappingSet.ResourceKeyById.TryGetValue(resourceKeyId, out ResourceKeyEntry? resourceKey))
        {
            throw ValidationFailure(
                DocumentCacheAdministrativeCommandClassification.InvalidRepresentationRestampMapping,
                DocumentCacheAdministrativeDiagnosticCategory.InvalidRepresentationRestampMapping,
                "Representation restamp document resource key is not present in the compiled mapping set."
            );
        }

        ConcreteResourceModel? model = mappingSet.TryGetDescriptorResourceModel(
            resourceKey.Resource,
            out ConcreteResourceModel? descriptorModel
        )
            ? descriptorModel
            : mappingSet.Model.ConcreteResourcesInNameOrder.SingleOrDefault(candidate =>
                candidate.ResourceKey.Resource.Equals(resourceKey.Resource)
            );
        if (model is null)
        {
            throw ValidationFailure(
                DocumentCacheAdministrativeCommandClassification.InvalidRepresentationRestampMapping,
                DocumentCacheAdministrativeDiagnosticCategory.InvalidRepresentationRestampMapping,
                "Representation restamp document resource is missing compiled metadata."
            );
        }
        if (model.ResourceKey.ResourceKeyId != resourceKeyId)
        {
            throw ValidationFailure(
                DocumentCacheAdministrativeCommandClassification.InvalidRepresentationRestampMapping,
                DocumentCacheAdministrativeDiagnosticCategory.InvalidRepresentationRestampMapping,
                "Representation restamp document resource key does not match compiled resource metadata."
            );
        }
        if (model.StorageKind is ResourceStorageKind.SharedDescriptorTable)
        {
            DbTableName descriptorTable = model.RelationalModel.Root.Table;
            return new RepresentationRestampMirrorRoute(
                resourceKeyId,
                descriptorTable.Schema.Value,
                descriptorTable.Name
            );
        }

        DbTriggerInfo? trigger = mappingSet.Model.TriggersInCreateOrder.SingleOrDefault(trigger =>
            trigger.Table.Equals(model.RelationalModel.Root.Table)
            && trigger.Parameters is TriggerKindParameters.DocumentStamping
        );
        if (trigger?.MirrorStampTargetTable is not { } mirrorTarget)
        {
            throw ValidationFailure(
                DocumentCacheAdministrativeCommandClassification.InvalidRepresentationRestampMirror,
                DocumentCacheAdministrativeDiagnosticCategory.InvalidRepresentationRestampMirror,
                "Representation restamp document resource has no unique compiled document-stamping mirror route."
            );
        }

        return new RepresentationRestampMirrorRoute(
            resourceKeyId,
            mirrorTarget.Schema.Value,
            mirrorTarget.Name
        );
    }

    public static TEnum ParseEnum<TEnum>(string value, string fieldName)
        where TEnum : struct, Enum =>
        Enum.TryParse(value, ignoreCase: false, out TEnum parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new InvalidOperationException(
                $"Representation restamp manifest contains an invalid {fieldName}."
            );

    public static void ValidatePageSize(int pageSize)
    {
        if (pageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageSize),
                "Representation restamp page size must be positive."
            );
        }
    }

    public static void RequireExactlyOne(int affected, string operation)
    {
        if (affected != 1)
        {
            throw new InvalidOperationException(
                $"Unable to {operation}; expected one affected row but observed {affected}."
            );
        }
    }

    public static RepresentationRestampValidationException ValidationFailure(
        DocumentCacheAdministrativeCommandClassification classification,
        DocumentCacheAdministrativeDiagnosticCategory diagnosticCategory,
        string message
    ) => new(classification, diagnosticCategory, message);
}
