// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// The natural key of a document reference: its declared target, which is the abstract resource for a
/// polymorphic reference, and its identity.
/// </summary>
/// <remarks>
/// <see cref="DocumentIdentity"/> wraps an array, so this struct's default equality compares array references.
/// Always key collections with <see cref="ReferenceLookupKeyComparer.Instance"/>.
/// </remarks>
internal readonly record struct ReferenceLookupKey(
    QualifiedResourceName TargetResource,
    DocumentIdentity DocumentIdentity
);

/// <summary>
/// Compares <see cref="ReferenceLookupKey"/> values structurally: the target, then every identity element's path
/// and value, ordinally and in order.
/// </summary>
internal sealed class ReferenceLookupKeyComparer : IEqualityComparer<ReferenceLookupKey>
{
    public static ReferenceLookupKeyComparer Instance { get; } = new();

    private ReferenceLookupKeyComparer() { }

    public bool Equals(ReferenceLookupKey x, ReferenceLookupKey y)
    {
        if (x.TargetResource != y.TargetResource)
        {
            return false;
        }

        var left = x.DocumentIdentity.DocumentIdentityElements;
        var right = y.DocumentIdentity.DocumentIdentityElements;

        if (left.Length != right.Length)
        {
            return false;
        }

        for (var index = 0; index < left.Length; index++)
        {
            if (
                !string.Equals(
                    left[index].IdentityJsonPath.Value,
                    right[index].IdentityJsonPath.Value,
                    StringComparison.Ordinal
                )
                || !string.Equals(
                    left[index].IdentityValue,
                    right[index].IdentityValue,
                    StringComparison.Ordinal
                )
            )
            {
                return false;
            }
        }

        return true;
    }

    public int GetHashCode(ReferenceLookupKey key)
    {
        HashCode hash = new();
        hash.Add(key.TargetResource);

        foreach (var element in key.DocumentIdentity.DocumentIdentityElements)
        {
            hash.Add(element.IdentityJsonPath.Value, StringComparer.Ordinal);
            hash.Add(element.IdentityValue, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }
}

/// <summary>
/// Resolved document references looked up by their natural key, independent of which identity array instance
/// the caller holds.
/// </summary>
internal interface IResolvedDocumentReferenceMap
{
    bool TryGetDocumentId(
        QualifiedResourceName targetResource,
        DocumentIdentity documentIdentity,
        out long documentId
    );
}

/// <summary>
/// The only construction path for <see cref="IResolvedDocumentReferenceMap"/>, so no map can be keyed with
/// default equality.
/// </summary>
internal interface IResolvedDocumentReferenceMapFactory
{
    IResolvedDocumentReferenceMap Create(IEnumerable<ResolvedDocumentReference> references);
}

internal sealed class ResolvedDocumentReferenceMapFactory : IResolvedDocumentReferenceMapFactory
{
    public IResolvedDocumentReferenceMap Create(IEnumerable<ResolvedDocumentReference> references)
    {
        ArgumentNullException.ThrowIfNull(references);

        Dictionary<ReferenceLookupKey, long> documentIdsByKey = new(ReferenceLookupKeyComparer.Instance);

        foreach (var resolved in references)
        {
            var targetResource = RelationalWriteSupport.ToQualifiedResourceName(
                resolved.Reference.ResourceInfo
            );
            var key = new ReferenceLookupKey(targetResource, resolved.Reference.DocumentIdentity);

            // The same identity may be referenced from several paths; it must resolve to one document.
            if (
                !documentIdsByKey.TryAdd(key, resolved.DocumentId)
                && documentIdsByKey[key] != resolved.DocumentId
            )
            {
                throw new InvalidOperationException(
                    $"Two references to '{RelationalWriteSupport.FormatResource(targetResource)}' with the same "
                        + $"identity resolved to different documents ({documentIdsByKey[key]} and {resolved.DocumentId})."
                );
            }
        }

        return new Map(documentIdsByKey);
    }

    private sealed class Map(Dictionary<ReferenceLookupKey, long> documentIdsByKey)
        : IResolvedDocumentReferenceMap
    {
        public bool TryGetDocumentId(
            QualifiedResourceName targetResource,
            DocumentIdentity documentIdentity,
            out long documentId
        )
        {
            ArgumentNullException.ThrowIfNull(documentIdentity);

            return documentIdsByKey.TryGetValue(
                new ReferenceLookupKey(targetResource, documentIdentity),
                out documentId
            );
        }
    }
}
