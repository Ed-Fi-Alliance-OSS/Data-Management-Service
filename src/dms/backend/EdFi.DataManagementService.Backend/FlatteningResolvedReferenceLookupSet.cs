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
    private readonly DocumentReferenceBinding[] _documentReferenceBindings;
    private readonly OrdinalPathMap<ResolvedDocumentReference>?[] _documentReferenceMapsByBindingIndex;
    private readonly Dictionary<string, IdentityBindingLookup>[] _identityBindingByColumnByBindingIndex;
    private readonly Dictionary<DescriptorLookupKey, OrdinalPathMap<long>> _descriptorIdMapsByKey;
    private readonly Dictionary<string, QualifiedResourceName> _descriptorResourceByPath;

    private readonly record struct IdentityBindingLookup(
        int IdentityBindingIndex,
        ReferenceIdentityBinding IdentityBinding
    );

    private FlatteningResolvedReferenceLookupSet(
        DocumentReferenceBinding[] documentReferenceBindings,
        OrdinalPathMap<ResolvedDocumentReference>?[] documentReferenceMapsByBindingIndex,
        Dictionary<string, IdentityBindingLookup>[] identityBindingByColumnByBindingIndex,
        Dictionary<DescriptorLookupKey, OrdinalPathMap<long>> descriptorIdMapsByKey,
        Dictionary<string, QualifiedResourceName> descriptorResourceByPath
    )
    {
        _documentReferenceBindings =
            documentReferenceBindings ?? throw new ArgumentNullException(nameof(documentReferenceBindings));
        _documentReferenceMapsByBindingIndex =
            documentReferenceMapsByBindingIndex
            ?? throw new ArgumentNullException(nameof(documentReferenceMapsByBindingIndex));
        _identityBindingByColumnByBindingIndex =
            identityBindingByColumnByBindingIndex
            ?? throw new ArgumentNullException(nameof(identityBindingByColumnByBindingIndex));
        _descriptorIdMapsByKey =
            descriptorIdMapsByKey ?? throw new ArgumentNullException(nameof(descriptorIdMapsByKey));
        _descriptorResourceByPath =
            descriptorResourceByPath ?? throw new ArgumentNullException(nameof(descriptorResourceByPath));
    }

    public static FlatteningResolvedReferenceLookupSet Create(
        ResourceWritePlan writePlan,
        ResolvedReferenceSet resolvedReferences
    )
    {
        ArgumentNullException.ThrowIfNull(writePlan);
        ArgumentNullException.ThrowIfNull(resolvedReferences);

        var documentReferenceBindings = writePlan.Model.DocumentReferenceBindings.ToArray();
        var documentBindingIndexByPath = documentReferenceBindings
            .Select((binding, index) => new { binding.ReferenceObjectPath, index })
            .ToDictionary(
                entry => entry.ReferenceObjectPath.Canonical,
                entry => entry.index,
                StringComparer.Ordinal
            );
        var documentReferenceMapsByBindingIndex = new OrdinalPathMap<ResolvedDocumentReference>?[
            documentReferenceBindings.Length
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

            var ordinalPathMap = documentReferenceMapsByBindingIndex[bindingIndex] ??=
                new OrdinalPathMap<ResolvedDocumentReference>();
            ordinalPathMap.Add(parsedPath.OrdinalPath, entry.Value);
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

        return new(
            documentReferenceBindings,
            documentReferenceMapsByBindingIndex,
            BuildIdentityBindingByColumnByBindingIndex(documentReferenceBindings),
            descriptorIdMapsByKey,
            BuildDescriptorResourceByPath(writePlan)
        );
    }

    public long? GetDocumentId(int bindingIndex, ReadOnlySpan<int> ordinalPath)
    {
        return GetResolvedDocumentReference(bindingIndex, ordinalPath)?.DocumentId;
    }

    public string? GetReferenceIdentityValue(
        ReferenceDerivedValueSourceMetadata referenceSource,
        DbColumnName columnName,
        ReadOnlySpan<int> ordinalPath
    )
    {
        ArgumentNullException.ThrowIfNull(referenceSource);

        return GetResolvedIdentityElement(
            referenceSource,
            columnName,
            ordinalPath,
            expectedDescriptorIdentity: false
        )?.IdentityValue;
    }

    public long? GetReferenceIdentityDescriptorId(
        ReferenceDerivedValueSourceMetadata referenceSource,
        DbColumnName columnName,
        ReadOnlySpan<int> ordinalPath
    )
    {
        ArgumentNullException.ThrowIfNull(referenceSource);

        if (
            GetResolvedIdentityElement(
                referenceSource,
                columnName,
                ordinalPath,
                expectedDescriptorIdentity: true
            )
            is null
        )
        {
            return null;
        }

        if (
            !_descriptorResourceByPath.TryGetValue(
                referenceSource.ReferenceJsonPath.Canonical,
                out var descriptorResource
            )
        )
        {
            throw new InvalidOperationException(
                $"Reference-derived logical member '{referenceSource.ReferenceJsonPath.Canonical}' on binding "
                    + $"'{referenceSource.ReferenceObjectPath.Canonical}' does not match any compiled descriptor path."
            );
        }

        var descriptorId = GetDescriptorId(
            descriptorResource,
            referenceSource.ReferenceJsonPath.Canonical,
            ordinalPath
        );

        if (descriptorId is not null)
        {
            return descriptorId.Value;
        }

        throw new InvalidOperationException(
            $"Reference-derived logical member '{referenceSource.ReferenceJsonPath.Canonical}' on binding "
                + $"'{referenceSource.ReferenceObjectPath.Canonical}' expected a resolved descriptor id at concrete path "
                + $"'{MaterializeConcretePath(referenceSource.ReferenceJsonPath.Canonical, ordinalPath)}', but the descriptor lookup set did not contain one."
        );
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

        foreach (var descriptorEdgeSource in writePlan.Model.DescriptorEdgeSources)
        {
            descriptorLookupKeys.Add(
                new DescriptorLookupKey(
                    descriptorEdgeSource.DescriptorResource,
                    descriptorEdgeSource.DescriptorValuePath.Canonical
                )
            );
        }

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

    private ResolvedDocumentReference? GetResolvedDocumentReference(
        int bindingIndex,
        ReadOnlySpan<int> ordinalPath
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bindingIndex);

        if (bindingIndex >= _documentReferenceMapsByBindingIndex.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bindingIndex),
                bindingIndex,
                "Document-reference binding index is out of range."
            );
        }

        var ordinalPathMap = _documentReferenceMapsByBindingIndex[bindingIndex];

        return ordinalPathMap is not null && ordinalPathMap.TryGet(ordinalPath, out var resolvedReference)
            ? resolvedReference
            : null;
    }

    private DocumentIdentityElement? GetResolvedIdentityElement(
        ReferenceDerivedValueSourceMetadata referenceSource,
        DbColumnName columnName,
        ReadOnlySpan<int> ordinalPath,
        bool expectedDescriptorIdentity
    )
    {
        var binding = GetDocumentReferenceBinding(referenceSource.BindingIndex);
        var resolvedReference = GetResolvedDocumentReference(referenceSource.BindingIndex, ordinalPath);

        if (resolvedReference is null)
        {
            return null;
        }

        var identityBindingLookup = GetIdentityBindingOrThrow(referenceSource, columnName);
        var identityElements = resolvedReference.Reference.DocumentIdentity.DocumentIdentityElements;

        if (identityElements.Length != binding.IdentityBindings.Count)
        {
            throw new InvalidOperationException(
                $"Resolved document-reference occurrence '{resolvedReference.Reference.Path.Value}' for binding "
                    + $"'{binding.ReferenceObjectPath.Canonical}' carried {identityElements.Length} identity value(s), "
                    + $"but compiled metadata expects {binding.IdentityBindings.Count}."
            );
        }

        var identityElement = ResolveIdentityElementByPath(
            binding,
            resolvedReference,
            referenceSource,
            identityBindingLookup,
            identityElements,
            expectedDescriptorIdentity
        );
        var isDescriptorIdentity = IsDescriptorIdentity(identityElement);

        if (isDescriptorIdentity == expectedDescriptorIdentity)
        {
            return identityElement;
        }

        var expectedKind = expectedDescriptorIdentity ? "descriptor" : "scalar";
        var actualKind = isDescriptorIdentity ? "descriptor" : "scalar";

        throw new InvalidOperationException(
            $"Resolved document-reference occurrence '{resolvedReference.Reference.Path.Value}' for binding "
                + $"'{binding.ReferenceObjectPath.Canonical}' resolved identity path "
                + $"'{identityElement.IdentityJsonPath.Value}' as {actualKind}, but compiled logical member "
                + $"'{referenceSource.ReferenceJsonPath.Canonical}' expects {expectedKind} identity metadata "
                + $"for identity path '{identityBindingLookup.IdentityBinding.IdentityJsonPath.Canonical}'."
        );
    }

    private static DocumentIdentityElement ResolveIdentityElementByPath(
        DocumentReferenceBinding binding,
        ResolvedDocumentReference resolvedReference,
        ReferenceDerivedValueSourceMetadata referenceSource,
        IdentityBindingLookup identityBindingLookup,
        IReadOnlyList<DocumentIdentityElement> identityElements,
        bool expectedDescriptorIdentity
    )
    {
        var expectedIdentityPath = identityBindingLookup.IdentityBinding.IdentityJsonPath.Canonical;
        var exactMatches = identityElements
            .Where(identityElement =>
                string.Equals(
                    identityElement.IdentityJsonPath.Value,
                    expectedIdentityPath,
                    StringComparison.Ordinal
                )
            )
            .Take(2)
            .ToArray();

        if (exactMatches.Length == 1)
        {
            return exactMatches[0];
        }

        if (exactMatches.Length > 1)
        {
            throw new InvalidOperationException(
                $"Resolved document-reference occurrence '{resolvedReference.Reference.Path.Value}' for binding "
                    + $"'{binding.ReferenceObjectPath.Canonical}' carried duplicate identity path "
                    + $"'{expectedIdentityPath}' for logical member '{referenceSource.ReferenceJsonPath.Canonical}'."
            );
        }

        if (expectedDescriptorIdentity && IsDescriptorIdentityPath(expectedIdentityPath))
        {
            var descriptorIdentityCount = identityElements.Count(IsDescriptorIdentity);
            var syntheticDescriptorMatches = identityElements
                .Where(identityElement =>
                    identityElement.IdentityJsonPath == DocumentIdentity.DescriptorIdentityJsonPath
                )
                .Take(2)
                .ToArray();

            if (syntheticDescriptorMatches.Length == 1 && descriptorIdentityCount == 1)
            {
                return syntheticDescriptorMatches[0];
            }

            if (syntheticDescriptorMatches.Length > 1 || descriptorIdentityCount > 1)
            {
                throw new InvalidOperationException(
                    $"Resolved document-reference occurrence '{resolvedReference.Reference.Path.Value}' for binding "
                        + $"'{binding.ReferenceObjectPath.Canonical}' could not safely match synthetic descriptor identity metadata "
                        + $"while logical member '{referenceSource.ReferenceJsonPath.Canonical}' expected identity path "
                        + $"'{expectedIdentityPath}'. Actual identity paths: {FormatIdentityPathList(identityElements)}."
                );
            }
        }

        var actualPathAtCompiledPosition = identityElements[identityBindingLookup.IdentityBindingIndex]
            .IdentityJsonPath
            .Value;

        throw new InvalidOperationException(
            $"Resolved document-reference occurrence '{resolvedReference.Reference.Path.Value}' for binding "
                + $"'{binding.ReferenceObjectPath.Canonical}' expected identity path '{expectedIdentityPath}' for "
                + $"logical member '{referenceSource.ReferenceJsonPath.Canonical}', but found "
                + $"'{actualPathAtCompiledPosition}' at compiled position {identityBindingLookup.IdentityBindingIndex}. "
                + $"Actual identity paths: {FormatIdentityPathList(identityElements)}."
        );
    }

    private static bool IsDescriptorIdentity(DocumentIdentityElement identityElement)
    {
        ArgumentNullException.ThrowIfNull(identityElement);

        return IsDescriptorIdentityPath(identityElement.IdentityJsonPath.Value);
    }

    private static bool IsDescriptorIdentityPath(string canonicalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalPath);

        return string.Equals(
                canonicalPath,
                DocumentIdentity.DescriptorIdentityJsonPath.Value,
                StringComparison.Ordinal
            )
            || canonicalPath.EndsWith("Descriptor", StringComparison.Ordinal)
            || canonicalPath.EndsWith("Descriptor]", StringComparison.Ordinal);
    }

    private static string FormatIdentityPathList(IReadOnlyList<DocumentIdentityElement> identityElements)
    {
        return string.Join(
            ", ",
            identityElements.Select(identityElement => $"'{identityElement.IdentityJsonPath.Value}'")
        );
    }

    private DocumentReferenceBinding GetDocumentReferenceBinding(int bindingIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bindingIndex);

        if (bindingIndex >= _documentReferenceBindings.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bindingIndex),
                bindingIndex,
                "Document-reference binding index is out of range."
            );
        }

        return _documentReferenceBindings[bindingIndex];
    }

    private IdentityBindingLookup GetIdentityBindingOrThrow(
        ReferenceDerivedValueSourceMetadata referenceSource,
        DbColumnName columnName
    )
    {
        var binding = GetDocumentReferenceBinding(referenceSource.BindingIndex);
        var identityBindingByColumn = _identityBindingByColumnByBindingIndex[referenceSource.BindingIndex];

        if (identityBindingByColumn.TryGetValue(columnName.Value, out var identityBindingLookup))
        {
            var identityBinding = identityBindingLookup.IdentityBinding;

            if (
                string.Equals(
                    identityBinding.ReferenceJsonPath.Canonical,
                    referenceSource.ReferenceJsonPath.Canonical,
                    StringComparison.Ordinal
                )
            )
            {
                return identityBindingLookup;
            }

            throw new InvalidOperationException(
                $"Reference-derived column '{columnName.Value}' on binding '{binding.ReferenceObjectPath.Canonical}' "
                    + $"was compiled for logical member '{identityBinding.ReferenceJsonPath.Canonical}', but runtime metadata requested "
                    + $"'{referenceSource.ReferenceJsonPath.Canonical}'."
            );
        }

        throw new InvalidOperationException(
            $"Reference-derived column '{columnName.Value}' does not match any compiled identity binding "
                + $"for reference binding '{binding.ReferenceObjectPath.Canonical}'."
        );
    }

    private static Dictionary<string, IdentityBindingLookup>[] BuildIdentityBindingByColumnByBindingIndex(
        IReadOnlyList<DocumentReferenceBinding> documentReferenceBindings
    )
    {
        var identityBindingByColumnByBindingIndex = new Dictionary<string, IdentityBindingLookup>[
            documentReferenceBindings.Count
        ];

        for (var bindingIndex = 0; bindingIndex < documentReferenceBindings.Count; bindingIndex++)
        {
            Dictionary<string, IdentityBindingLookup> identityBindingByColumn = new(StringComparer.Ordinal);
            var binding = documentReferenceBindings[bindingIndex];

            for (
                var identityBindingIndex = 0;
                identityBindingIndex < binding.IdentityBindings.Count;
                identityBindingIndex++
            )
            {
                if (
                    !identityBindingByColumn.TryAdd(
                        binding.IdentityBindings[identityBindingIndex].Column.Value,
                        new IdentityBindingLookup(
                            identityBindingIndex,
                            binding.IdentityBindings[identityBindingIndex]
                        )
                    )
                )
                {
                    throw new InvalidOperationException(
                        $"Reference binding '{binding.ReferenceObjectPath.Canonical}' contains duplicate identity binding column "
                            + $"'{binding.IdentityBindings[identityBindingIndex].Column.Value}'."
                    );
                }
            }

            identityBindingByColumnByBindingIndex[bindingIndex] = identityBindingByColumn;
        }

        return identityBindingByColumnByBindingIndex;
    }

    private static Dictionary<string, QualifiedResourceName> BuildDescriptorResourceByPath(
        ResourceWritePlan writePlan
    )
    {
        Dictionary<string, QualifiedResourceName> descriptorResourceByPath = new(StringComparer.Ordinal);

        foreach (var descriptorEdgeSource in writePlan.Model.DescriptorEdgeSources)
        {
            if (
                descriptorResourceByPath.TryGetValue(
                    descriptorEdgeSource.DescriptorValuePath.Canonical,
                    out var existingResource
                )
            )
            {
                if (!existingResource.Equals(descriptorEdgeSource.DescriptorResource))
                {
                    throw new InvalidOperationException(
                        $"Descriptor path '{descriptorEdgeSource.DescriptorValuePath.Canonical}' was compiled for multiple descriptor resources: "
                            + $"'{RelationalWriteSupport.FormatResource(existingResource)}' and "
                            + $"'{RelationalWriteSupport.FormatResource(descriptorEdgeSource.DescriptorResource)}'."
                    );
                }

                continue;
            }

            descriptorResourceByPath.Add(
                descriptorEdgeSource.DescriptorValuePath.Canonical,
                descriptorEdgeSource.DescriptorResource
            );
        }

        return descriptorResourceByPath;
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

    private static string MaterializeConcretePath(string wildcardPath, ReadOnlySpan<int> ordinalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wildcardPath);

        System.Text.StringBuilder concretePath = new(wildcardPath.Length + ordinalPath.Length * 3);
        var ordinalIndex = 0;
        var pathIndex = 0;

        while (pathIndex < wildcardPath.Length)
        {
            if (
                pathIndex <= wildcardPath.Length - 3
                && wildcardPath[pathIndex] == '['
                && wildcardPath[pathIndex + 1] == '*'
                && wildcardPath[pathIndex + 2] == ']'
            )
            {
                if (ordinalIndex >= ordinalPath.Length)
                {
                    throw new InvalidOperationException(
                        $"Path '{wildcardPath}' requires more ordinal components than were supplied."
                    );
                }

                concretePath.Append('[').Append(ordinalPath[ordinalIndex]).Append(']');
                ordinalIndex++;
                pathIndex += 3;
                continue;
            }

            concretePath.Append(wildcardPath[pathIndex]);
            pathIndex++;
        }

        if (ordinalIndex != ordinalPath.Length)
        {
            throw new InvalidOperationException(
                $"Path '{wildcardPath}' received {ordinalPath.Length} ordinal components, but only {ordinalIndex} wildcards were available."
            );
        }

        return concretePath.ToString();
    }

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
