// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Old.Postgresql.Model;

namespace EdFi.DataManagementService.Old.Postgresql;

internal static class ReferenceHelper
{
    /// <summary>
    /// Returns the ReferentialId Guids and corresponding partition keys for all of the document
    /// references in the UpdateRequest and UpsertRequest.
    /// </summary>
    public static DocumentReferenceIds DocumentReferenceIdsFrom(DocumentReference[] documentReferences)
    {
        Guid[] referentialIds = documentReferences.Select(x => x.ReferentialId.Value).ToArray();
        short[] referentialPartitionKeys = documentReferences
            .Select(x => PartitionUtility.PartitionKeyFor(x.ReferentialId).Value)
            .ToArray();
        return new(referentialIds, referentialPartitionKeys);
    }

    /// <summary>
    /// Returns the ReferentialId Guids and corresponding partition keys for all of the descriptor
    /// references in the UpdateRequest and UpsertRequest.
    /// </summary>
    public static DocumentReferenceIds DescriptorReferenceIdsFrom(DescriptorReference[] descriptorReferences)
    {
        Guid[] referentialIds = descriptorReferences.Select(x => x.ReferentialId.Value).ToArray();
        short[] referentialPartitionKeys = descriptorReferences
            .Select(x => PartitionUtility.PartitionKeyFor(x.ReferentialId).Value)
            .ToArray();
        return new(referentialIds, referentialPartitionKeys);
    }

    /// <summary>
    /// Returns invalid document references for all matching referential ids without collapsing
    /// duplicate occurrences at different JSON paths.
    /// </summary>
    public static DocumentReferenceFailure[] DocumentReferenceFailuresFrom(
        DocumentReference[] documentReferences,
        Guid[] referentialIds,
        DocumentReferenceFailureReason reason
    )
    {
        HashSet<Guid> referentialIdSet = [.. referentialIds];

        return documentReferences
            .Where(documentReference => referentialIdSet.Contains(documentReference.ReferentialId.Value))
            .Select(documentReference => DocumentReferenceFailure.From(documentReference, reason))
            .ToArray();
    }

    /// <summary>
    /// Returns invalid descriptor references for all matching referential ids without collapsing
    /// duplicate occurrences at different JSON paths.
    /// </summary>
    public static DescriptorReferenceFailure[] DescriptorReferenceFailuresFrom(
        DescriptorReference[] descriptorReferences,
        Guid[] referentialIds
    )
    {
        HashSet<Guid> referentialIdSet = [.. referentialIds];

        return descriptorReferences
            .Where(descriptorReference => referentialIdSet.Contains(descriptorReference.ReferentialId.Value))
            .Select(descriptorReference =>
                DescriptorReferenceFailure.From(
                    descriptorReference,
                    DetermineDescriptorReferenceFailureReason(descriptorReference)
                )
            )
            .ToArray();
    }

    private static DescriptorReferenceFailureReason DetermineDescriptorReferenceFailureReason(
        DescriptorReference descriptorReference
    )
    {
        var descriptorIdentity =
            descriptorReference.DocumentIdentity.DocumentIdentityElements.SingleOrDefault()
            ?? throw new InvalidOperationException(
                $"Descriptor reference at path '{descriptorReference.Path.Value}' is missing its descriptor identity value."
            );

        return
            TryGetDescriptorTypeName(descriptorIdentity.IdentityValue, out var descriptorTypeName)
            && !string.Equals(
                descriptorTypeName,
                descriptorReference.ResourceInfo.ResourceName.Value,
                StringComparison.OrdinalIgnoreCase
            )
            ? DescriptorReferenceFailureReason.DescriptorTypeMismatch
            : DescriptorReferenceFailureReason.Missing;
    }

    private static bool TryGetDescriptorTypeName(string descriptorUri, out string descriptorTypeName)
    {
        var fragmentIndex = descriptorUri.IndexOf('#', StringComparison.Ordinal);
        var lastSlashIndex = descriptorUri.LastIndexOf('/');

        if (lastSlashIndex < 0 || fragmentIndex <= lastSlashIndex + 1)
        {
            descriptorTypeName = string.Empty;
            return false;
        }

        descriptorTypeName = descriptorUri[(lastSlashIndex + 1)..fragmentIndex];
        return !string.IsNullOrWhiteSpace(descriptorTypeName);
    }
}
