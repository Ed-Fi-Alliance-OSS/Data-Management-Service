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
            .Select(DescriptorReferenceFailureClassifier.Missing)
            .ToArray();
    }
}
