// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Request-scoped relational reference resolver that memoizes referential-id lookups for the current request.
/// </summary>
public sealed class ReferenceResolver(IReferenceResolverAdapter adapter) : IReferenceResolver
{
    private readonly IReferenceResolverAdapter _adapter =
        adapter ?? throw new ArgumentNullException(nameof(adapter));

    private readonly Dictionary<ReferentialId, ReferenceLookupSnapshot> _memoizedLookups = [];

    public async Task<ResolvedReferenceSet> ResolveAsync(
        ReferenceResolverRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.MappingSet);
        ArgumentNullException.ThrowIfNull(request.DocumentReferences);
        ArgumentNullException.ThrowIfNull(request.DescriptorReferences);

        var uncachedReferentialIds = GetUncachedReferentialIds(request);

        if (uncachedReferentialIds.Count > 0)
        {
            var lookupResults = await _adapter
                .ResolveAsync(
                    new ReferenceLookupRequest(
                        MappingSet: request.MappingSet,
                        RequestResource: request.RequestResource,
                        ReferentialIds: uncachedReferentialIds
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);

            CacheLookupResults(uncachedReferentialIds, lookupResults);
        }

        return BuildResolvedReferenceSet(request);
    }

    private List<ReferentialId> GetUncachedReferentialIds(ReferenceResolverRequest request)
    {
        HashSet<ReferentialId> seenReferentialIds = [];
        List<ReferentialId> uncachedReferentialIds = [];

        foreach (var documentReference in request.DocumentReferences)
        {
            AddUncachedReferentialId(
                documentReference.ReferentialId,
                seenReferentialIds,
                uncachedReferentialIds
            );
        }

        foreach (var descriptorReference in request.DescriptorReferences)
        {
            AddUncachedReferentialId(
                descriptorReference.ReferentialId,
                seenReferentialIds,
                uncachedReferentialIds
            );
        }

        return uncachedReferentialIds;
    }

    private void AddUncachedReferentialId(
        ReferentialId referentialId,
        ISet<ReferentialId> seenReferentialIds,
        ICollection<ReferentialId> uncachedReferentialIds
    )
    {
        if (!seenReferentialIds.Add(referentialId) || _memoizedLookups.ContainsKey(referentialId))
        {
            return;
        }

        uncachedReferentialIds.Add(referentialId);
    }

    private void CacheLookupResults(
        IReadOnlyCollection<ReferentialId> requestedReferentialIds,
        IReadOnlyList<ReferenceLookupResult> lookupResults
    )
    {
        ArgumentNullException.ThrowIfNull(requestedReferentialIds);
        ArgumentNullException.ThrowIfNull(lookupResults);

        Dictionary<ReferentialId, ReferenceLookupResult> lookupResultByReferentialId = [];
        HashSet<ReferentialId> requestedReferentialIdSet = [.. requestedReferentialIds];

        foreach (var lookupResult in lookupResults)
        {
            if (!requestedReferentialIdSet.Contains(lookupResult.ReferentialId))
            {
                throw new InvalidOperationException(
                    $"Reference resolver adapter returned an unexpected referential id '{lookupResult.ReferentialId.Value}'."
                );
            }

            if (!lookupResultByReferentialId.TryAdd(lookupResult.ReferentialId, lookupResult))
            {
                throw new InvalidOperationException(
                    $"Reference resolver adapter returned multiple results for referential id '{lookupResult.ReferentialId.Value}'."
                );
            }
        }

        foreach (var referentialId in requestedReferentialIds)
        {
            lookupResultByReferentialId.TryGetValue(referentialId, out var lookupResult);
            _memoizedLookups[referentialId] = new ReferenceLookupSnapshot(referentialId, lookupResult);
        }
    }

    private ResolvedReferenceSet BuildResolvedReferenceSet(ReferenceResolverRequest request)
    {
        var documentReferenceOccurrences = request
            .DocumentReferences.Select(documentReference => new ResolvedDocumentReferenceOccurrence(
                documentReference,
                GetRequiredLookupSnapshot(documentReference.ReferentialId)
            ))
            .ToArray();

        var descriptorReferenceOccurrences = request
            .DescriptorReferences.Select(descriptorReference => new ResolvedDescriptorReferenceOccurrence(
                descriptorReference,
                GetRequiredLookupSnapshot(descriptorReference.ReferentialId)
            ))
            .ToArray();

        Dictionary<JsonPath, ResolvedDocumentReference> successfulDocumentReferencesByPath = [];

        foreach (var documentReferenceOccurrence in documentReferenceOccurrences)
        {
            var lookupResult = documentReferenceOccurrence.Lookup.Result;

            if (lookupResult is null)
            {
                continue;
            }

            AddSuccessfulDocumentReference(
                successfulDocumentReferencesByPath,
                documentReferenceOccurrence.Reference,
                lookupResult
            );
        }

        Dictionary<JsonPath, ResolvedDescriptorReference> successfulDescriptorReferencesByPath = [];
        Dictionary<DescriptorReferenceKey, long> descriptorDocumentIdByKey = [];

        foreach (var descriptorReferenceOccurrence in descriptorReferenceOccurrences)
        {
            var lookupResult = descriptorReferenceOccurrence.Lookup.Result;

            if (lookupResult is null || !lookupResult.IsDescriptor)
            {
                continue;
            }

            var descriptorKey = CreateDescriptorReferenceKey(descriptorReferenceOccurrence.Reference);

            if (
                descriptorDocumentIdByKey.TryGetValue(descriptorKey, out var existingDocumentId)
                && existingDocumentId != lookupResult.DocumentId
            )
            {
                throw new InvalidOperationException(
                    $"Descriptor reference key '{descriptorKey.NormalizedUri}' for resource "
                        + $"'{descriptorKey.DescriptorResource.ProjectName}/{descriptorKey.DescriptorResource.ResourceName}' "
                        + "resolved to multiple document ids within the same request."
                );
            }

            descriptorDocumentIdByKey[descriptorKey] = lookupResult.DocumentId;

            AddSuccessfulDescriptorReference(
                successfulDescriptorReferencesByPath,
                descriptorReferenceOccurrence.Reference,
                lookupResult
            );
        }

        Dictionary<ReferentialId, ReferenceLookupSnapshot> lookupsByReferentialId = [];

        foreach (var documentReferenceOccurrence in documentReferenceOccurrences)
        {
            lookupsByReferentialId[documentReferenceOccurrence.Reference.ReferentialId] =
                documentReferenceOccurrence.Lookup;
        }

        foreach (var descriptorReferenceOccurrence in descriptorReferenceOccurrences)
        {
            lookupsByReferentialId[descriptorReferenceOccurrence.Reference.ReferentialId] =
                descriptorReferenceOccurrence.Lookup;
        }

        return new ResolvedReferenceSet(
            SuccessfulDocumentReferencesByPath: successfulDocumentReferencesByPath,
            SuccessfulDescriptorReferencesByPath: successfulDescriptorReferencesByPath,
            LookupsByReferentialId: lookupsByReferentialId,
            DocumentReferenceOccurrences: documentReferenceOccurrences,
            DescriptorReferenceOccurrences: descriptorReferenceOccurrences
        );
    }

    private static void AddSuccessfulDocumentReference(
        IDictionary<JsonPath, ResolvedDocumentReference> successfulReferencesByPath,
        DocumentReference documentReference,
        ReferenceLookupResult lookupResult
    )
    {
        if (
            !successfulReferencesByPath.TryAdd(
                documentReference.Path,
                new ResolvedDocumentReference(
                    Reference: documentReference,
                    DocumentId: lookupResult.DocumentId,
                    ResourceKeyId: lookupResult.ResourceKeyId
                )
            )
        )
        {
            throw new InvalidOperationException(
                $"Document reference path '{documentReference.Path.Value}' was extracted more than once within the same request."
            );
        }
    }

    private static void AddSuccessfulDescriptorReference(
        IDictionary<JsonPath, ResolvedDescriptorReference> successfulReferencesByPath,
        DescriptorReference descriptorReference,
        ReferenceLookupResult lookupResult
    )
    {
        if (
            !successfulReferencesByPath.TryAdd(
                descriptorReference.Path,
                new ResolvedDescriptorReference(
                    Reference: descriptorReference,
                    DocumentId: lookupResult.DocumentId,
                    ResourceKeyId: lookupResult.ResourceKeyId
                )
            )
        )
        {
            throw new InvalidOperationException(
                $"Descriptor reference path '{descriptorReference.Path.Value}' was extracted more than once within the same request."
            );
        }
    }

    private ReferenceLookupSnapshot GetRequiredLookupSnapshot(ReferentialId referentialId)
    {
        if (_memoizedLookups.TryGetValue(referentialId, out var lookupSnapshot))
        {
            return lookupSnapshot;
        }

        throw new InvalidOperationException(
            $"Reference resolver did not cache referential id '{referentialId.Value}' before materializing the result set."
        );
    }

    private static DescriptorReferenceKey CreateDescriptorReferenceKey(
        DescriptorReference descriptorReference
    )
    {
        var descriptorIdentity =
            descriptorReference.DocumentIdentity.DocumentIdentityElements.SingleOrDefault()
            ?? throw new InvalidOperationException(
                $"Descriptor reference at path '{descriptorReference.Path.Value}' is missing its descriptor identity value."
            );

        return new DescriptorReferenceKey(
            NormalizedUri: descriptorIdentity.IdentityValue.ToLowerInvariant(),
            DescriptorResource: ToQualifiedResourceName(descriptorReference.ResourceInfo)
        );
    }

    private static QualifiedResourceName ToQualifiedResourceName(BaseResourceInfo resourceInfo) =>
        new(resourceInfo.ProjectName.Value, resourceInfo.ResourceName.Value);
}
