// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Postgresql.Model;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend.Postgresql;

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
    /// Returns the unique ResourceNames of all DocumentReferences that have the given ReferentialId Guids
    /// </summary>
    public static ResourceName[] ResourceNamesFrom(
        DocumentReference[] documentReferences,
        Guid[] referentialIds
    )
    {
        Dictionary<Guid, string> guidToResourceNameMap =
            new(
                documentReferences.Select(x => new KeyValuePair<Guid, string>(
                    x.ReferentialId.Value,
                    x.ResourceInfo.ResourceName.Value
                ))
            );

        HashSet<string> uniqueResourceNames = [];

        foreach (Guid referentialId in referentialIds)
        {
            if (guidToResourceNameMap.TryGetValue(referentialId, out string? value))
            {
                uniqueResourceNames.Add(value);
            }
        }
        return uniqueResourceNames.Select(x => new ResourceName(x)).ToArray();
    }

    /// <summary>
    /// Returns a list of descriptor references filtered by referentialId
    /// </summary>
    public static List<DescriptorReference> DescriptorReferencesWithReferentialIds(
        DescriptorReference[] descriptorReferences,
        Guid[] referentialIds
    )
    {
        return descriptorReferences
            .Where(d => referentialIds.Contains(d.ReferentialId.Value))
            .ToList();
    }
}
