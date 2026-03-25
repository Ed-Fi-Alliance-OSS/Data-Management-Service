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
        List<DocumentReferenceFailure> invalidDocumentReferences = [];

        foreach (var documentReferenceOccurrence in documentReferenceOccurrences)
        {
            var documentReferenceFailure = ClassifyDocumentReferenceFailure(
                request.MappingSet,
                documentReferenceOccurrence
            );

            if (documentReferenceFailure is not null)
            {
                invalidDocumentReferences.Add(documentReferenceFailure);
                continue;
            }

            AddSuccessfulDocumentReference(
                successfulDocumentReferencesByPath,
                documentReferenceOccurrence.Reference,
                documentReferenceOccurrence.Lookup.Result!
            );
        }

        Dictionary<JsonPath, ResolvedDescriptorReference> successfulDescriptorReferencesByPath = [];
        Dictionary<DescriptorReferenceKey, long> descriptorDocumentIdByKey = [];
        List<DescriptorReferenceFailure> invalidDescriptorReferences = [];

        foreach (var descriptorReferenceOccurrence in descriptorReferenceOccurrences)
        {
            var descriptorReferenceFailure = ClassifyDescriptorReferenceFailure(
                request.MappingSet,
                descriptorReferenceOccurrence
            );

            if (descriptorReferenceFailure is not null)
            {
                invalidDescriptorReferences.Add(descriptorReferenceFailure);
                continue;
            }

            var lookupResult = descriptorReferenceOccurrence.Lookup.Result!;
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
    )
    {
        var lookupResult = descriptorReferenceOccurrence.Lookup.Result;

        if (lookupResult is null)
        {
            return DescriptorReferenceFailure.From(
                descriptorReferenceOccurrence.Reference,
                DetermineMissingDescriptorReferenceFailureReason(descriptorReferenceOccurrence.Reference)
            );
        }

        return IsCompatibleDescriptorTarget(mappingSet, descriptorReferenceOccurrence.Reference, lookupResult)
            ? null
            : DescriptorReferenceFailure.From(
                descriptorReferenceOccurrence.Reference,
                DescriptorReferenceFailureReason.DescriptorTypeMismatch
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

        var targetResource = ToQualifiedResourceName(descriptorReference.ResourceInfo);

        if (!mappingSet.ResourceKeyIdByResource.TryGetValue(targetResource, out var expectedResourceKeyId))
        {
            throw new InvalidOperationException(
                $"Descriptor reference target '{targetResource.ProjectName}/{targetResource.ResourceName}' is missing from ResourceKeyIdByResource in mapping set "
                    + $"'{mappingSet.Key.EffectiveSchemaHash}/{mappingSet.Key.Dialect}/{mappingSet.Key.RelationalMappingVersion}'."
            );
        }

        return lookupResult.ResourceKeyId == expectedResourceKeyId;
    }

    private static DescriptorReferenceFailureReason DetermineMissingDescriptorReferenceFailureReason(
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
