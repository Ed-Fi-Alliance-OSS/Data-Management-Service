// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Backend;
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
    private readonly Dictionary<ReferentialId, ReferenceLookupRequestEntry> _memoizedLookupRequests = [];

    public async Task<ResolvedReferenceSet> ResolveAsync(
        ReferenceResolverRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.MappingSet);
        ArgumentNullException.ThrowIfNull(request.DocumentReferences);
        ArgumentNullException.ThrowIfNull(request.DescriptorReferences);

        var uncachedLookups = GetUncachedLookups(request);

        if (uncachedLookups.Count > 0)
        {
            var lookupResults = await _adapter
                .ResolveAsync(
                    new ReferenceLookupRequest(
                        MappingSet: request.MappingSet,
                        RequestResource: request.RequestResource,
                        Lookups: uncachedLookups
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);

            CacheLookupResults(
                uncachedLookups.Select(static lookup => lookup.ReferentialId).ToArray(),
                lookupResults
            );
            CacheLookupRequests(uncachedLookups);
        }

        return BuildResolvedReferenceSet(request);
    }

    private List<ReferenceLookupRequestEntry> GetUncachedLookups(ReferenceResolverRequest request)
    {
        Dictionary<ReferentialId, ReferenceLookupRequestEntry> seenLookupByReferentialId = [];
        List<ReferenceLookupRequestEntry> uncachedLookups = [];

        foreach (var documentReference in request.DocumentReferences)
        {
            AddUncachedLookup(
                CreateLookupRequestEntry(documentReference),
                seenLookupByReferentialId,
                uncachedLookups
            );
        }

        foreach (var descriptorReference in request.DescriptorReferences)
        {
            AddUncachedLookup(
                CreateLookupRequestEntry(descriptorReference),
                seenLookupByReferentialId,
                uncachedLookups
            );
        }

        return uncachedLookups;
    }

    private void AddUncachedLookup(
        ReferenceLookupRequestEntry lookup,
        IDictionary<ReferentialId, ReferenceLookupRequestEntry> seenLookupByReferentialId,
        ICollection<ReferenceLookupRequestEntry> uncachedLookups
    )
    {
        if (
            seenLookupByReferentialId.TryGetValue(lookup.ReferentialId, out var seenLookup)
            || _memoizedLookupRequests.TryGetValue(lookup.ReferentialId, out seenLookup)
        )
        {
            EnsureCompatibleLookupRequest(seenLookup, lookup);
            return;
        }

        seenLookupByReferentialId[lookup.ReferentialId] = lookup;

        if (_memoizedLookups.ContainsKey(lookup.ReferentialId))
        {
            return;
        }

        uncachedLookups.Add(lookup);
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

    private void CacheLookupRequests(IReadOnlyCollection<ReferenceLookupRequestEntry> lookupRequests)
    {
        ArgumentNullException.ThrowIfNull(lookupRequests);

        foreach (var lookupRequest in lookupRequests)
        {
            _memoizedLookupRequests[lookupRequest.ReferentialId] = lookupRequest;
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
        List<DocumentReferenceFailure> invalidDocumentReferences = [];

        foreach (var documentReferenceOccurrence in documentReferenceOccurrences)
        {
            var lookupResult = documentReferenceOccurrence.Lookup.Result;
            EnsureLookupIntegrity(
                request.MappingSet,
                documentReferenceOccurrence.Reference.Path,
                documentReferenceOccurrence.Reference.ReferentialId,
                lookupResult
            );

            var documentReferenceFailure = ClassifyDocumentReferenceFailure(
                request.MappingSet,
                documentReferenceOccurrence
            );

            if (documentReferenceFailure is not null)
            {
                invalidDocumentReferences.Add(documentReferenceFailure);
                continue;
            }

            if (lookupResult is null)
            {
                throw new InvalidOperationException(
                    $"Document reference at path '{documentReferenceOccurrence.Reference.Path.Value}' was classified as successful without a lookup result."
                );
            }

            AddSuccessfulDocumentReference(
                successfulDocumentReferencesByPath,
                documentReferenceOccurrence.Reference,
                lookupResult
            );
        }

        Dictionary<JsonPath, ResolvedDescriptorReference> successfulDescriptorReferencesByPath = [];
        Dictionary<DescriptorReferenceKey, long> descriptorDocumentIdByKey = [];
        List<DescriptorReferenceFailure> invalidDescriptorReferences = [];

        foreach (var descriptorReferenceOccurrence in descriptorReferenceOccurrences)
        {
            var lookupResult = descriptorReferenceOccurrence.Lookup.Result;
            EnsureLookupIntegrity(
                request.MappingSet,
                descriptorReferenceOccurrence.Reference.Path,
                descriptorReferenceOccurrence.Reference.ReferentialId,
                lookupResult
            );

            var descriptorReferenceFailure = ClassifyDescriptorReferenceFailure(
                request.MappingSet,
                descriptorReferenceOccurrence
            );

            if (descriptorReferenceFailure is not null)
            {
                invalidDescriptorReferences.Add(descriptorReferenceFailure);
                continue;
            }

            if (lookupResult is null)
            {
                throw new InvalidOperationException(
                    $"Descriptor reference at path '{descriptorReferenceOccurrence.Reference.Path.Value}' was classified as successful without a lookup result."
                );
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
            InvalidDocumentReferences: invalidDocumentReferences,
            InvalidDescriptorReferences: invalidDescriptorReferences,
            DocumentReferenceOccurrences: documentReferenceOccurrences,
            DescriptorReferenceOccurrences: descriptorReferenceOccurrences
        );
    }

    private static DocumentReferenceFailure? ClassifyDocumentReferenceFailure(
        MappingSet mappingSet,
        ResolvedDocumentReferenceOccurrence documentReferenceOccurrence
    )
    {
        var lookupResult = documentReferenceOccurrence.Lookup.Result;

        if (lookupResult is null)
        {
            return DocumentReferenceFailure.From(
                documentReferenceOccurrence.Reference,
                DocumentReferenceFailureReason.Missing
            );
        }

        var targetMetadata = mappingSet.GetDocumentReferenceTargetMetadataOrThrow(
            documentReferenceOccurrence.Reference.ResourceInfo
        );

        return targetMetadata.AllowsResourceKeyId(lookupResult.ResourceKeyId)
            ? null
            : DocumentReferenceFailure.From(
                documentReferenceOccurrence.Reference,
                DocumentReferenceFailureReason.IncompatibleTargetType
            );
    }

    private static DescriptorReferenceFailure? ClassifyDescriptorReferenceFailure(
        MappingSet mappingSet,
        ResolvedDescriptorReferenceOccurrence descriptorReferenceOccurrence
    ) =>
        DescriptorReferenceFailureClassifier.Classify(
            mappingSet,
            descriptorReferenceOccurrence.Reference,
            descriptorReferenceOccurrence.Lookup.Result
        );

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

    private ReferenceLookupRequestEntry GetRequiredLookupRequestEntry(ReferentialId referentialId)
    {
        if (_memoizedLookupRequests.TryGetValue(referentialId, out var lookupRequestEntry))
        {
            return lookupRequestEntry;
        }

        throw new InvalidOperationException(
            $"Reference resolver did not cache lookup metadata for referential id '{referentialId.Value}' before materializing the result set."
        );
    }

    private void EnsureLookupIntegrity(
        MappingSet mappingSet,
        JsonPath path,
        ReferentialId referentialId,
        ReferenceLookupResult? lookupResult
    )
    {
        if (lookupResult is null)
        {
            return;
        }

        EnsureLookupIntegrity(mappingSet, path, GetRequiredLookupRequestEntry(referentialId), lookupResult);
    }

    private static void EnsureLookupIntegrity(
        MappingSet mappingSet,
        JsonPath path,
        ReferenceLookupRequestEntry lookupRequest,
        ReferenceLookupResult lookupResult
    )
    {
        if (
            string.Equals(
                lookupRequest.ExpectedVerificationIdentityKey,
                lookupResult.VerificationIdentityKey,
                StringComparison.Ordinal
            )
        )
        {
            return;
        }

        var resolvedResource = mappingSet.ResourceKeyById.TryGetValue(
            lookupResult.ResourceKeyId,
            out var resolvedResourceKey
        )
            ? $"{resolvedResourceKey.Resource.ProjectName}/{resolvedResourceKey.Resource.ResourceName}"
            : $"ResourceKeyId={lookupResult.ResourceKeyId}";

        throw new ReferenceLookupCorruptionException(
            $"Reference lookup corruption detected for referential id '{lookupRequest.ReferentialId.Value}' at path '{path.Value}': "
                + $"requested target '{lookupRequest.RequestedResource.ProjectName}/{lookupRequest.RequestedResource.ResourceName}' "
                + $"expected verification key '{lookupRequest.ExpectedVerificationIdentityKey}', but the resolved dms.ReferentialIdentity row "
                + $"returned document id '{lookupResult.DocumentId}', resource '{resolvedResource}', and verification key "
                + $"'{lookupResult.VerificationIdentityKey ?? "<null>"}'."
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

    private static ReferenceLookupRequestEntry CreateLookupRequestEntry(
        DocumentReference documentReference
    ) =>
        new(
            documentReference.ReferentialId,
            ToQualifiedResourceName(documentReference.ResourceInfo),
            documentReference.DocumentIdentity,
            ReferenceLookupVerificationSupport.BuildExpectedVerificationIdentityKey(
                documentReference.DocumentIdentity
            )
        );

    private static ReferenceLookupRequestEntry CreateLookupRequestEntry(
        DescriptorReference descriptorReference
    ) =>
        new(
            descriptorReference.ReferentialId,
            ToQualifiedResourceName(descriptorReference.ResourceInfo),
            descriptorReference.DocumentIdentity,
            ReferenceLookupVerificationSupport.BuildExpectedVerificationIdentityKey(
                descriptorReference.DocumentIdentity,
                normalizeDescriptorValues: true
            )
        );

    private static void EnsureCompatibleLookupRequest(
        ReferenceLookupRequestEntry existingLookup,
        ReferenceLookupRequestEntry candidateLookup
    )
    {
        if (
            existingLookup.RequestedResource == candidateLookup.RequestedResource
            && existingLookup.ExpectedVerificationIdentityKey
                == candidateLookup.ExpectedVerificationIdentityKey
        )
        {
            return;
        }

        throw new InvalidOperationException(
            $"Reference resolver received conflicting lookup metadata for referential id '{candidateLookup.ReferentialId.Value}'."
        );
    }
}
