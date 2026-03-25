// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Allowed document-reference target resource keys derived from mapping-set metadata.
/// </summary>
/// <param name="TargetResource">The document-reference target resource.</param>
/// <param name="AllowedResourceKeyIds">
/// Allowed concrete <c>ResourceKeyId</c> values in deterministic order.
/// </param>
internal sealed record DocumentReferenceTargetMetadata(
    QualifiedResourceName TargetResource,
    IReadOnlyList<short> AllowedResourceKeyIds
)
{
    private readonly FrozenSet<short> _allowedResourceKeyIds = AllowedResourceKeyIds.ToFrozenSet();

    public bool AllowsResourceKeyId(short resourceKeyId) => _allowedResourceKeyIds.Contains(resourceKeyId);
}

/// <summary>
/// Resolves document-reference compatibility metadata from the selected runtime mapping set.
/// </summary>
internal static class MappingSetDocumentReferenceCompatibilityExtensions
{
    private static readonly ConditionalWeakTable<
        MappingSet,
        FrozenDictionary<QualifiedResourceName, DocumentReferenceTargetMetadata>
    > MetadataByResourceByMappingSet = new();

    public static DocumentReferenceTargetMetadata GetDocumentReferenceTargetMetadataOrThrow(
        this MappingSet mappingSet,
        BaseResourceInfo targetResource
    )
    {
        ArgumentNullException.ThrowIfNull(targetResource);

        return mappingSet.GetDocumentReferenceTargetMetadataOrThrow(ToQualifiedResourceName(targetResource));
    }

    public static DocumentReferenceTargetMetadata GetDocumentReferenceTargetMetadataOrThrow(
        this MappingSet mappingSet,
        QualifiedResourceName targetResource
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);

        var metadataByResource = MetadataByResourceByMappingSet.GetValue(mappingSet, BuildMetadataByResource);

        if (metadataByResource.TryGetValue(targetResource, out var metadata))
        {
            return metadata;
        }

        throw CreateMissingMetadataException(mappingSet, targetResource);
    }

    private static FrozenDictionary<
        QualifiedResourceName,
        DocumentReferenceTargetMetadata
    > BuildMetadataByResource(MappingSet mappingSet)
    {
        Dictionary<QualifiedResourceName, DocumentReferenceTargetMetadata> metadataByResource = [];

        foreach (var concreteResourceModel in mappingSet.Model.ConcreteResourcesInNameOrder)
        {
            if (concreteResourceModel.StorageKind is ResourceStorageKind.SharedDescriptorTable)
            {
                continue;
            }

            var resourceKeyId = GetValidatedResourceKeyIdOrThrow(
                mappingSet,
                concreteResourceModel.ResourceKey
            );

            AddMetadataOrThrow(
                metadataByResource,
                mappingSet,
                concreteResourceModel.ResourceKey.Resource,
                [resourceKeyId],
                "ConcreteResourcesInNameOrder"
            );
        }

        foreach (var abstractUnionView in mappingSet.Model.AbstractUnionViewsInNameOrder)
        {
            var abstractResourceKey = abstractUnionView.AbstractResourceKey;

            if (!abstractResourceKey.IsAbstractResource)
            {
                throw new InvalidOperationException(
                    $"{FormatLookupPrefix(mappingSet, abstractResourceKey.Resource)}: "
                        + "AbstractUnionViewsInNameOrder contains a non-abstract target resource."
                );
            }

            _ = GetValidatedResourceKeyIdOrThrow(mappingSet, abstractResourceKey);

            List<short> allowedResourceKeyIds = [];
            HashSet<short> seenResourceKeyIds = [];

            foreach (
                var memberResourceKey in abstractUnionView.UnionArmsInOrder.Select(static unionArm =>
                    unionArm.ConcreteMemberResourceKey
                )
            )
            {
                if (memberResourceKey.IsAbstractResource)
                {
                    throw new InvalidOperationException(
                        $"{FormatLookupPrefix(mappingSet, abstractResourceKey.Resource)}: "
                            + $"abstract union view member '{FormatResource(memberResourceKey.Resource)}' must be concrete."
                    );
                }

                var memberResourceKeyId = GetValidatedResourceKeyIdOrThrow(mappingSet, memberResourceKey);

                if (!seenResourceKeyIds.Add(memberResourceKeyId))
                {
                    throw new InvalidOperationException(
                        $"{FormatLookupPrefix(mappingSet, abstractResourceKey.Resource)}: "
                            + $"duplicate concrete member resource key id '{memberResourceKeyId}' was emitted by AbstractUnionViewsInNameOrder."
                    );
                }

                allowedResourceKeyIds.Add(memberResourceKeyId);
            }

            if (allowedResourceKeyIds.Count == 0)
            {
                throw new InvalidOperationException(
                    $"{FormatLookupPrefix(mappingSet, abstractResourceKey.Resource)}: "
                        + "abstract target does not have any concrete member resource keys."
                );
            }

            AddMetadataOrThrow(
                metadataByResource,
                mappingSet,
                abstractResourceKey.Resource,
                allowedResourceKeyIds.ToArray(),
                "AbstractUnionViewsInNameOrder"
            );
        }

        return metadataByResource.ToFrozenDictionary();
    }

    private static void AddMetadataOrThrow(
        IDictionary<QualifiedResourceName, DocumentReferenceTargetMetadata> metadataByResource,
        MappingSet mappingSet,
        QualifiedResourceName targetResource,
        IReadOnlyList<short> allowedResourceKeyIds,
        string sourceCollection
    )
    {
        if (
            !metadataByResource.TryAdd(
                targetResource,
                new DocumentReferenceTargetMetadata(targetResource, allowedResourceKeyIds)
            )
        )
        {
            throw new InvalidOperationException(
                $"{FormatLookupPrefix(mappingSet, targetResource)}: "
                    + $"multiple compatibility metadata entries were emitted from '{sourceCollection}'."
            );
        }
    }

    private static short GetValidatedResourceKeyIdOrThrow(
        MappingSet mappingSet,
        ResourceKeyEntry expectedResourceKey
    )
    {
        var targetResource = expectedResourceKey.Resource;

        if (!mappingSet.ResourceKeyIdByResource.TryGetValue(targetResource, out var resourceKeyId))
        {
            throw new InvalidOperationException(
                $"{FormatLookupPrefix(mappingSet, targetResource)}: "
                    + "ResourceKeyIdByResource is missing an entry for the target resource."
            );
        }

        if (resourceKeyId != expectedResourceKey.ResourceKeyId)
        {
            throw new InvalidOperationException(
                $"{FormatLookupPrefix(mappingSet, targetResource)}: "
                    + $"ResourceKeyIdByResource returned '{resourceKeyId}' but derived metadata expects "
                    + $"'{expectedResourceKey.ResourceKeyId}'."
            );
        }

        if (!mappingSet.ResourceKeyById.TryGetValue(resourceKeyId, out var actualResourceKey))
        {
            throw new InvalidOperationException(
                $"{FormatLookupPrefix(mappingSet, targetResource)}: "
                    + $"ResourceKeyById is missing an entry for resource key id '{resourceKeyId}'."
            );
        }

        if (actualResourceKey != expectedResourceKey)
        {
            throw new InvalidOperationException(
                $"{FormatLookupPrefix(mappingSet, targetResource)}: "
                    + $"ResourceKeyById entry for id '{resourceKeyId}' does not match the derived metadata entry."
            );
        }

        return resourceKeyId;
    }

    private static Exception CreateMissingMetadataException(
        MappingSet mappingSet,
        QualifiedResourceName targetResource
    )
    {
        if (!mappingSet.ResourceKeyIdByResource.TryGetValue(targetResource, out var resourceKeyId))
        {
            return new InvalidOperationException(
                $"{FormatLookupPrefix(mappingSet, targetResource)}: "
                    + "ResourceKeyIdByResource is missing an entry for the target resource."
            );
        }

        if (!mappingSet.ResourceKeyById.TryGetValue(resourceKeyId, out var resourceKey))
        {
            return new InvalidOperationException(
                $"{FormatLookupPrefix(mappingSet, targetResource)}: "
                    + $"ResourceKeyById is missing an entry for resource key id '{resourceKeyId}'."
            );
        }

        if (resourceKey.Resource != targetResource)
        {
            return new InvalidOperationException(
                $"{FormatLookupPrefix(mappingSet, targetResource)}: "
                    + $"ResourceKeyById entry for id '{resourceKeyId}' resolves to "
                    + $"'{FormatResource(resourceKey.Resource)}' instead of the requested target resource."
            );
        }

        if (TryGetConcreteResourceModel(mappingSet, targetResource, out var concreteResourceModel))
        {
            if (concreteResourceModel.StorageKind is ResourceStorageKind.SharedDescriptorTable)
            {
                return new InvalidOperationException(
                    $"{FormatLookupPrefix(mappingSet, targetResource)}: "
                        + "target resource is a descriptor resource and is excluded from document-reference compatibility checks."
                );
            }

            return new InvalidOperationException(
                $"{FormatLookupPrefix(mappingSet, targetResource)}: "
                    + "ConcreteResourcesInNameOrder contains the target resource, but no compatibility metadata entry was produced."
            );
        }

        if (resourceKey.IsAbstractResource)
        {
            return new InvalidOperationException(
                $"{FormatLookupPrefix(mappingSet, targetResource)}: "
                    + "target resource is abstract but AbstractUnionViewsInNameOrder does not contain a matching entry."
            );
        }

        return new InvalidOperationException(
            $"{FormatLookupPrefix(mappingSet, targetResource)}: "
                + "target resource was not found in ConcreteResourcesInNameOrder."
        );
    }

    private static bool TryGetConcreteResourceModel(
        MappingSet mappingSet,
        QualifiedResourceName targetResource,
        out ConcreteResourceModel concreteResourceModel
    )
    {
        foreach (var candidate in mappingSet.Model.ConcreteResourcesInNameOrder)
        {
            if (candidate.ResourceKey.Resource == targetResource)
            {
                concreteResourceModel = candidate;
                return true;
            }
        }

        concreteResourceModel = null!;
        return false;
    }

    private static QualifiedResourceName ToQualifiedResourceName(BaseResourceInfo targetResource) =>
        new(targetResource.ProjectName.Value, targetResource.ResourceName.Value);

    private static string FormatLookupPrefix(MappingSet mappingSet, QualifiedResourceName targetResource) =>
        $"Document-reference compatibility metadata lookup failed for target '{FormatResource(targetResource)}' in mapping set '{FormatMappingSetKey(mappingSet.Key)}'";

    private static string FormatResource(QualifiedResourceName resource) =>
        $"{resource.ProjectName}.{resource.ResourceName}";

    private static string FormatMappingSetKey(MappingSetKey key) =>
        $"{key.EffectiveSchemaHash}/{key.Dialect}/{key.RelationalMappingVersion}";
}
