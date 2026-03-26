// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

internal static class DescriptorReferenceFailureClassifier
{
    public static DescriptorReferenceFailure? Classify(
        MappingSet mappingSet,
        DescriptorReference descriptorReference,
        ReferenceLookupResult? lookupResult
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(descriptorReference);

        if (lookupResult is null)
        {
            return Missing(descriptorReference);
        }

        return IsCompatibleDescriptorTarget(mappingSet, descriptorReference, lookupResult)
            ? null
            : DescriptorReferenceFailure.From(
                descriptorReference,
                DescriptorReferenceFailureReason.DescriptorTypeMismatch
            );
    }

    public static DescriptorReferenceFailure Missing(DescriptorReference descriptorReference)
    {
        ArgumentNullException.ThrowIfNull(descriptorReference);

        return DescriptorReferenceFailure.From(descriptorReference, DescriptorReferenceFailureReason.Missing);
    }

    private static bool IsCompatibleDescriptorTarget(
        MappingSet mappingSet,
        DescriptorReference descriptorReference,
        ReferenceLookupResult lookupResult
    )
    {
        if (!lookupResult.IsDescriptor)
        {
            return false;
        }

        var targetResource = new QualifiedResourceName(
            descriptorReference.ResourceInfo.ProjectName.Value,
            descriptorReference.ResourceInfo.ResourceName.Value
        );

        if (!mappingSet.ResourceKeyIdByResource.TryGetValue(targetResource, out var expectedResourceKeyId))
        {
            throw new InvalidOperationException(
                $"Descriptor reference target '{targetResource.ProjectName}/{targetResource.ResourceName}' is missing from ResourceKeyIdByResource in mapping set "
                    + $"'{mappingSet.Key.EffectiveSchemaHash}/{mappingSet.Key.Dialect}/{mappingSet.Key.RelationalMappingVersion}'."
            );
        }

        return lookupResult.ResourceKeyId == expectedResourceKeyId;
    }
}
