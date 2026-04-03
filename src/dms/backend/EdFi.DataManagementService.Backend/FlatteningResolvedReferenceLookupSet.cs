// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

internal sealed class FlatteningResolvedReferenceLookupSet
{
    private readonly OrdinalPathMap<long>?[] _documentIdMapsByBindingIndex;
    private readonly Dictionary<DescriptorLookupKey, OrdinalPathMap<long>> _descriptorIdMapsByKey;

    private FlatteningResolvedReferenceLookupSet(
        OrdinalPathMap<long>?[] documentIdMapsByBindingIndex,
        Dictionary<DescriptorLookupKey, OrdinalPathMap<long>> descriptorIdMapsByKey
    )
    {
        _documentIdMapsByBindingIndex =
            documentIdMapsByBindingIndex
            ?? throw new ArgumentNullException(nameof(documentIdMapsByBindingIndex));
        _descriptorIdMapsByKey =
            descriptorIdMapsByKey ?? throw new ArgumentNullException(nameof(descriptorIdMapsByKey));
    }

    public static FlatteningResolvedReferenceLookupSet Create(
        ResourceWritePlan writePlan,
        ResolvedReferenceSet resolvedReferences
    )
    {
        ArgumentNullException.ThrowIfNull(writePlan);
        ArgumentNullException.ThrowIfNull(resolvedReferences);

        var documentBindingIndexByPath = writePlan
            .Model.DocumentReferenceBindings.Select(
                (binding, index) => new { binding.ReferenceObjectPath, index }
            )
            .ToDictionary(
                entry => entry.ReferenceObjectPath.Canonical,
                entry => entry.index,
                StringComparer.Ordinal
            );
        var documentIdMapsByBindingIndex = new OrdinalPathMap<long>?[
            writePlan.Model.DocumentReferenceBindings.Count
        ];

        foreach (var entry in resolvedReferences.SuccessfulDocumentReferencesByPath)
        {
            var parsedPath = RelationalJsonPathSupport.ParseConcretePath(entry.Key);

            if (!documentBindingIndexByPath.TryGetValue(parsedPath.WildcardPath, out var bindingIndex))
            {
                throw new InvalidOperationException(
                    $"Resolved document-reference path '{entry.Key.Value}' did not match any compiled document-reference binding for resource '{RelationalWriteSupport.FormatResource(writePlan.Model.Resource)}'."
                );
            }

            var ordinalPathMap = documentIdMapsByBindingIndex[bindingIndex] ??= new OrdinalPathMap<long>();
            ordinalPathMap.Add(parsedPath.OrdinalPath, entry.Value.DocumentId);
        }

        var descriptorLookupKeys = GetDescriptorLookupKeys(writePlan);
        Dictionary<DescriptorLookupKey, OrdinalPathMap<long>> descriptorIdMapsByKey = [];

        foreach (var entry in resolvedReferences.SuccessfulDescriptorReferencesByPath)
        {
            var parsedPath = RelationalJsonPathSupport.ParseConcretePath(entry.Key);
            var descriptorResource = RelationalWriteSupport.ToQualifiedResourceName(
                entry.Value.Reference.ResourceInfo
            );
            var lookupKey = new DescriptorLookupKey(descriptorResource, parsedPath.WildcardPath);

            if (!descriptorLookupKeys.Contains(lookupKey))
            {
                throw new InvalidOperationException(
                    $"Resolved descriptor-reference path '{entry.Key.Value}' did not match any compiled descriptor binding for resource '{RelationalWriteSupport.FormatResource(writePlan.Model.Resource)}'."
                );
            }

            var ordinalPathMap = descriptorIdMapsByKey.GetValueOrDefault(lookupKey);

            if (ordinalPathMap is null)
            {
                ordinalPathMap = new OrdinalPathMap<long>();
                descriptorIdMapsByKey.Add(lookupKey, ordinalPathMap);
            }

            ordinalPathMap.Add(parsedPath.OrdinalPath, entry.Value.DocumentId);
        }

        return new(documentIdMapsByBindingIndex, descriptorIdMapsByKey);
    }

    public long? GetDocumentId(int bindingIndex, ReadOnlySpan<int> ordinalPath)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bindingIndex);

        if (bindingIndex >= _documentIdMapsByBindingIndex.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bindingIndex),
                bindingIndex,
                "Document-reference binding index is out of range."
            );
        }

        var ordinalPathMap = _documentIdMapsByBindingIndex[bindingIndex];

        return ordinalPathMap is not null && ordinalPathMap.TryGet(ordinalPath, out var documentId)
            ? documentId
            : null;
    }

    public long? GetDescriptorId(
        TableWritePlan tableWritePlan,
        WriteValueSource.DescriptorReference descriptorReference,
        ReadOnlySpan<int> ordinalPath
    )
    {
        ArgumentNullException.ThrowIfNull(tableWritePlan);
        ArgumentNullException.ThrowIfNull(descriptorReference);

        return GetDescriptorId(
            descriptorReference.DescriptorResource,
            GetDescriptorLookupPath(tableWritePlan, descriptorReference),
            ordinalPath
        );
    }

    public long? GetDescriptorId(
        QualifiedResourceName descriptorResource,
        string wildcardPath,
        ReadOnlySpan<int> ordinalPath
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wildcardPath);

        var lookupKey = new DescriptorLookupKey(descriptorResource, wildcardPath);

        return
            _descriptorIdMapsByKey.TryGetValue(lookupKey, out var ordinalPathMap)
            && ordinalPathMap.TryGet(ordinalPath, out var descriptorId)
            ? descriptorId
            : null;
    }

    private static HashSet<DescriptorLookupKey> GetDescriptorLookupKeys(ResourceWritePlan writePlan)
    {
        HashSet<DescriptorLookupKey> descriptorLookupKeys = [];

        foreach (var tableWritePlan in writePlan.TablePlansInDependencyOrder)
        {
            foreach (var columnBinding in tableWritePlan.ColumnBindings)
            {
                if (columnBinding.Source is not WriteValueSource.DescriptorReference descriptorReference)
                {
                    continue;
                }

                descriptorLookupKeys.Add(
                    new DescriptorLookupKey(
                        descriptorReference.DescriptorResource,
                        GetDescriptorLookupPath(tableWritePlan, descriptorReference)
                    )
                );
            }

            foreach (var keyUnificationPlan in tableWritePlan.KeyUnificationPlans)
            {
                foreach (
                    var descriptorMember in keyUnificationPlan.MembersInOrder.OfType<KeyUnificationMemberWritePlan.DescriptorMember>()
                )
                {
                    descriptorLookupKeys.Add(
                        new DescriptorLookupKey(
                            descriptorMember.DescriptorResource,
                            GetDescriptorLookupPath(tableWritePlan, descriptorMember)
                        )
                    );
                }
            }
        }

        return descriptorLookupKeys;
    }

    private static string GetDescriptorLookupPath(
        TableWritePlan tableWritePlan,
        WriteValueSource.DescriptorReference descriptorReference
    )
    {
        if (descriptorReference.DescriptorValuePath is { } descriptorValuePath)
        {
            return descriptorValuePath.Canonical;
        }

        JsonPathSegment[] combinedSegments =
        [
            .. tableWritePlan.TableModel.JsonScope.Segments,
            .. descriptorReference.RelativePath.Segments,
        ];

        return RelationalJsonPathSupport.BuildCanonical(combinedSegments);
    }

    private static string GetDescriptorLookupPath(
        TableWritePlan tableWritePlan,
        KeyUnificationMemberWritePlan.DescriptorMember descriptorMember
    )
    {
        JsonPathSegment[] combinedSegments =
        [
            .. tableWritePlan.TableModel.JsonScope.Segments,
            .. descriptorMember.RelativePath.Segments,
        ];

        return RelationalJsonPathSupport.BuildCanonical(combinedSegments);
    }

    private readonly record struct DescriptorLookupKey(
        QualifiedResourceName DescriptorResource,
        string WildcardPath
    );

    private sealed class OrdinalPathMap<TValue>
    {
        private readonly Dictionary<int, List<Entry>> _entriesByHash = [];

        public void Add(int[] ordinalPath, TValue value)
        {
            var hash = OrdinalPathHash.Hash(ordinalPath);

            if (!_entriesByHash.TryGetValue(hash, out var entries))
            {
                entries = [];
                _entriesByHash.Add(hash, entries);
            }

            entries.Add(new Entry(ordinalPath, value));
        }

        public bool TryGet(ReadOnlySpan<int> ordinalPath, out TValue value)
        {
            var hash = OrdinalPathHash.Hash(ordinalPath);

            if (!_entriesByHash.TryGetValue(hash, out var entries))
            {
                value = default!;
                return false;
            }

            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];

                if (ordinalPath.SequenceEqual(entry.OrdinalPath))
                {
                    value = entry.Value;
                    return true;
                }
            }

            value = default!;
            return false;
        }

        private readonly record struct Entry(int[] OrdinalPath, TValue Value);
    }

    private static class OrdinalPathHash
    {
        public static int Hash(ReadOnlySpan<int> ordinalPath)
        {
            HashCode hashCode = new();
            hashCode.Add(ordinalPath.Length);

            for (var i = 0; i < ordinalPath.Length; i++)
            {
                hashCode.Add(ordinalPath[i]);
            }

            return hashCode.ToHashCode();
        }

        public static int Hash(int[] ordinalPath)
        {
            ArgumentNullException.ThrowIfNull(ordinalPath);
            return Hash(ordinalPath.AsSpan());
        }
    }
}
