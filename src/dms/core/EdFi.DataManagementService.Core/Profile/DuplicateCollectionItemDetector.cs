// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Detects duplicate visible collection items that share the same compiled
/// semantic identity within the same stable parent address. Duplicates are
/// rejected with C8 category-3 typed failures before backend receives the contract.
/// </summary>
public static class DuplicateCollectionItemDetector
{
    /// <summary>
    /// Checks for duplicate visible collection items and returns category-3
    /// failures for any collisions found.
    /// </summary>
    public static ImmutableArray<WritableProfileValidationFailure> Detect(
        ImmutableArray<VisibleRequestCollectionItem> items,
        string profileName,
        string resourceName,
        string method,
        string operation
    )
    {
        if (items.Length <= 1)
        {
            return [];
        }

        // Group items by their CollectionRowAddress using structural equality
        var groups = new Dictionary<CollectionRowAddress, List<VisibleRequestCollectionItem>>(
            CollectionRowAddressComparer.Instance
        );

        foreach (var item in items)
        {
            if (!groups.TryGetValue(item.Address, out var group))
            {
                group = [];
                groups[item.Address] = group;
            }
            group.Add(item);
        }

        ImmutableArray<WritableProfileValidationFailure>.Builder? failures = null;

        foreach (var (address, group) in groups)
        {
            if (group.Count > 1)
            {
                failures ??= ImmutableArray.CreateBuilder<WritableProfileValidationFailure>();

                // The factory requires at least 2 request paths. Generate index-based
                // placeholder paths since the detector does not track per-item JSON paths.
                IEnumerable<string> placeholderPaths = Enumerable
                    .Range(0, group.Count)
                    .Select(i => $"{address.JsonScope}[{i}]");

                failures.Add(
                    ProfileFailures.DuplicateVisibleCollectionItemCollision(
                        profileName: profileName,
                        resourceName: resourceName,
                        method: method,
                        operation: operation,
                        jsonScope: address.JsonScope,
                        stableParentAddress: address.ParentAddress,
                        semanticIdentityPartsInOrder: address.SemanticIdentityInOrder,
                        requestJsonPaths: placeholderPaths
                    )
                );
            }
        }

        return failures?.ToImmutable() ?? [];
    }
}

/// <summary>
/// Structural equality comparer for CollectionRowAddress that handles
/// ImmutableArray fields by comparing elements.
/// </summary>
internal sealed class CollectionRowAddressComparer : IEqualityComparer<CollectionRowAddress>
{
    public static readonly CollectionRowAddressComparer Instance = new();

    public bool Equals(CollectionRowAddress? x, CollectionRowAddress? y)
    {
        if (ReferenceEquals(x, y))
        {
            return true;
        }

        if (x is null || y is null)
        {
            return false;
        }

        if (x.JsonScope != y.JsonScope)
        {
            return false;
        }

        if (!ScopeInstanceAddressEquals(x.ParentAddress, y.ParentAddress))
        {
            return false;
        }

        return SemanticIdentityEquals(x.SemanticIdentityInOrder, y.SemanticIdentityInOrder);
    }

    public int GetHashCode(CollectionRowAddress obj)
    {
        var hash = new HashCode();
        hash.Add(obj.JsonScope);
        hash.Add(obj.ParentAddress.JsonScope);
        foreach (var part in obj.SemanticIdentityInOrder)
        {
            hash.Add(part.RelativePath);
            hash.Add(part.Value?.ToJsonString());
            hash.Add(part.IsPresent);
        }
        return hash.ToHashCode();
    }

    private static bool ScopeInstanceAddressEquals(ScopeInstanceAddress a, ScopeInstanceAddress b)
    {
        if (a.JsonScope != b.JsonScope)
        {
            return false;
        }

        if (a.AncestorCollectionInstances.Length != b.AncestorCollectionInstances.Length)
        {
            return false;
        }

        for (int i = 0; i < a.AncestorCollectionInstances.Length; i++)
        {
            var ai = a.AncestorCollectionInstances[i];
            var bi = b.AncestorCollectionInstances[i];

            if (ai.JsonScope != bi.JsonScope)
            {
                return false;
            }

            if (!SemanticIdentityEquals(ai.SemanticIdentityInOrder, bi.SemanticIdentityInOrder))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SemanticIdentityEquals(
        ImmutableArray<SemanticIdentityPart> a,
        ImmutableArray<SemanticIdentityPart> b
    )
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        for (int i = 0; i < a.Length; i++)
        {
            if (a[i].RelativePath != b[i].RelativePath)
            {
                return false;
            }

            if (a[i].IsPresent != b[i].IsPresent)
            {
                return false;
            }

            if (a[i].Value?.ToJsonString() != b[i].Value?.ToJsonString())
            {
                return false;
            }
        }

        return true;
    }
}
